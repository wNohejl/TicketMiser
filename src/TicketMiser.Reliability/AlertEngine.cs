using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Reliability;

/// <summary>A condition detected during evaluation, before it is reconciled against open alerts.</summary>
public record AlertCandidate(string RuleKey, int? SourceId, AlertSeverity Severity, string Message, int? EventId = null);

public static class AlertRules
{
    // Platform rules: about a source.
    public const string Freshness = "freshness";
    public const string SuccessRate = "success_rate";
    public const string VolumeAnomaly = "volume_anomaly";
    public const string BudgetPressure = "budget_pressure";

    // Watch rules: about an event a person is tracking.
    public const string PriceDrop = "price_drop";
    public const string TargetReached = "target_reached";
    public const string PrimaryReappeared = "primary_reappeared";
}

/// <summary>
/// Evaluates KPI rules and reconciles the result against currently open alerts.
///
/// The reconciliation is the important part: a rule that is still failing must not spam a
/// new row every cycle, and a rule that has recovered must auto-resolve without anyone
/// clicking. So each (rule, source, event) triple has at most one open alert at a time.
/// </summary>
public class AlertEngine(
    TicketMiserDbContext db,
    KpiCalculator kpi,
    BudgetCalculator budget,
    IOptions<ReliabilityOptions> options,
    ILogger<AlertEngine> logger)
{
    private readonly ReliabilityOptions _options = options.Value;

    public async Task<IReadOnlyList<AlertCandidate>> EvaluateAsync(CancellationToken ct = default)
    {
        var candidates = new List<AlertCandidate>();
        var sources = await db.Sources.Where(s => s.Enabled).ToListAsync(ct);

        foreach (var source in sources)
        {
            var health = await kpi.GetHealthAsync(
                source, _options.SuccessRateWindow, _options.VolumeBaselineDays, ct);

            // A source that has never run is not "stale"; it is unconfigured. Alerting on it
            // would be noise on a fresh clone, which is how alert fatigue starts.
            if (health.NeverRun)
                continue;

            if (health.IsStale(_options.FreshnessSlo))
            {
                var age = health.FreshnessMinutes is { } m ? $"{m / 60:F1}h" : "never";

                candidates.Add(new AlertCandidate(
                    AlertRules.Freshness, source.Id, AlertSeverity.Critical,
                    $"{source.Name}: no successful ingestion for {age} (SLO {_options.FreshnessSlo.TotalHours:F0}h)."));
            }

            if (health.RunsInWindow >= 3 && health.SuccessRate < _options.SuccessRateSlo)
            {
                candidates.Add(new AlertCandidate(
                    AlertRules.SuccessRate, source.Id, AlertSeverity.Warn,
                    $"{source.Name}: success rate {health.SuccessRate:P0} over {health.RunsInWindow} runs "
                    + $"(SLO {_options.SuccessRateSlo:P0})."));
            }

            if (health.VolumeRatio is { } ratio && ratio < _options.VolumeAnomalyThreshold)
            {
                candidates.Add(new AlertCandidate(
                    AlertRules.VolumeAnomaly, source.Id, AlertSeverity.Warn,
                    $"{source.Name}: today's volume is {ratio:P0} of the trailing median; "
                    + "possible upstream schema drift."));
            }

            if (await BudgetPressureAsync(source, ct) is { } pressure)
                candidates.Add(pressure);
        }

        candidates.AddRange(await EvaluateWatchesAsync(sources, ct));

        await ReconcileAsync(candidates, ct);
        return candidates;
    }

    /// <summary>
    /// The three rules about events. Cheap by construction: a watchlist is tens of rows, and
    /// each rule reads the newest two observations or the last week of ticks for it.
    /// </summary>
    public async Task<IReadOnlyList<AlertCandidate>> EvaluateWatchesAsync(
        IReadOnlyList<Source> sources, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var kinds = sources.ToDictionary(s => s.Id, s => s);
        var candidates = new List<AlertCandidate>();

        var watches = await db.Watches
            .Include(w => w.Event)
            .Where(w => w.Enabled && w.Event!.StartsAt > now)
            .ToListAsync(ct);

        // One pass per watched event, not per watch: the rules are about the event, and each
        // (rule, source, event) has at most one open alert whoever watches it. With several
        // owners on one event, a drop is raised if any of them asked for drops, and the target
        // rule fires on the highest target — someone's target has been reached. Per-owner
        // targets and who hears about them are the delivery's to decide, not the alert's.
        foreach (var group in watches.GroupBy(w => w.EventId))
        {
            var eventId = group.Key;
            var name = group.First().Event!.Name;
            var notifyOnDrop = group.Any(w => w.NotifyOnDrop);
            var targetPrice = group.Max(w => w.TargetPrice);

            // Newest two observations per source, in one query per watch.
            var recent = await db.PriceObservations
                .Where(o => o.EventId == eventId && o.ObservedAt >= now.AddDays(-2))
                .OrderByDescending(o => o.ObservedAt)
                .ToListAsync(ct);

            var latestBySource = recent.GroupBy(o => o.SourceId)
                .Select(g => (Latest: g.First(), Previous: g.Skip(1).FirstOrDefault()))
                .ToList();

            if (notifyOnDrop)
            {
                foreach (var (latest, previous) in latestBySource)
                {
                    if (previous is null || latest.Lowest is null || previous.Lowest is null)
                        continue;

                    if (latest.Lowest < previous.Lowest && latest.ObservedAt >= now.AddHours(-24))
                    {
                        var source = kinds.GetValueOrDefault(latest.SourceId);
                        candidates.Add(new AlertCandidate(
                            AlertRules.PriceDrop, latest.SourceId, AlertSeverity.Info,
                            $"{name}: {source?.Name ?? "a source"} fell from {previous.Lowest:C0} to {latest.Lowest:C0}"
                            + (latest.AllIn ? " all-in." : " (fees not included)."),
                            eventId));
                    }
                }
            }

            if (targetPrice is { } target)
            {
                // A target is an all-in number. A face-value price under it is not a hit.
                var hit = latestBySource
                    .Select(p => p.Latest)
                    .Where(o => o.AllIn && o.Lowest is not null && o.Lowest <= target)
                    .OrderBy(o => o.Lowest)
                    .FirstOrDefault();

                if (hit is not null)
                {
                    var source = kinds.GetValueOrDefault(hit.SourceId);
                    candidates.Add(new AlertCandidate(
                        AlertRules.TargetReached, hit.SourceId, AlertSeverity.Info,
                        $"{name}: {hit.Lowest:C0} all-in at {source?.Name ?? "a source"}, target was {target:C0}.",
                        eventId));
                }
            }

            // Primary went to not-available and later came back, inside the last week.
            var ticks = await db.OnSaleTicks
                .Where(t => t.EventId == eventId && t.PrimaryStatus != null && t.ObservedAt >= now.AddDays(-7))
                .OrderBy(t => t.ObservedAt)
                .Select(t => new { t.SourceId, t.ObservedAt, t.PrimaryStatus })
                .ToListAsync(ct);

            var goneAt = ticks.FirstOrDefault(t => t.PrimaryStatus == InventoryStatus.NotAvailable);
            if (goneAt is not null)
            {
                var back = ticks.FirstOrDefault(t =>
                    t.ObservedAt > goneAt.ObservedAt && t.PrimaryStatus == InventoryStatus.Available);

                if (back is not null)
                {
                    candidates.Add(new AlertCandidate(
                        AlertRules.PrimaryReappeared, back.SourceId, AlertSeverity.Warn,
                        $"{name}: primary tickets reappeared at {back.ObservedAt:HH:mm} UTC, "
                        + $"{(back.ObservedAt - goneAt.ObservedAt).TotalHours:F1}h after selling out.",
                        eventId));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Raises budget pressure for a provider approaching or past its ceiling.
    ///
    /// Neither severity is Critical, so neither auto-opens an incident: exhausting a free tier
    /// is a planned consequence of the cost model, not an outage to be paged on.
    /// </summary>
    private async Task<AlertCandidate?> BudgetPressureAsync(Source source, CancellationToken ct)
    {
        var usage = await budget.GetUsageAsync(source, ct);

        if (usage.IsUnmetered || usage.Worst is not { } worst)
            return null;

        if (worst.Ratio < _options.BudgetWarnThreshold)
            return null;

        var exhausted = worst.Ratio >= 1.0;
        var dimension = BudgetUsage.Describe(worst.Dimension);

        return new AlertCandidate(
            AlertRules.BudgetPressure, source.Id,
            exhausted ? AlertSeverity.Warn : AlertSeverity.Info,
            exhausted
                ? $"{source.Name}: {dimension} budget exhausted ({worst.Used}/{worst.Limit}); runs are being refused."
                : $"{source.Name}: {worst.Ratio:P0} of the {dimension} budget used ({worst.Used}/{worst.Limit}).");
    }

    /// <summary>
    /// Opens alerts for new conditions, leaves existing ones alone, and resolves any open
    /// alert whose condition no longer appears in this cycle's candidates.
    /// </summary>
    private async Task ReconcileAsync(IReadOnlyList<AlertCandidate> candidates, CancellationToken ct)
    {
        var open = await db.Alerts.Where(a => a.ResolvedAt == null).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;

        static bool Same(Alert a, AlertCandidate c)
            => a.RuleKey == c.RuleKey && a.SourceId == c.SourceId && a.EventId == c.EventId;

        foreach (var candidate in candidates)
        {
            var existing = open.FirstOrDefault(a => Same(a, candidate));

            if (existing is not null)
            {
                // Keep the message and severity current; the numbers in it drift as the
                // condition continues, and a rule can escalate in place.
                existing.Message = candidate.Message;
                existing.Severity = candidate.Severity;
                continue;
            }

            var opened = new Alert
            {
                RuleKey = candidate.RuleKey,
                SourceId = candidate.SourceId,
                EventId = candidate.EventId,
                Severity = candidate.Severity,
                Message = candidate.Message,
                TriggeredAt = now
            };
            db.Alerts.Add(opened);

            // Open from here on, so an identical candidate later in this pass updates it
            // instead of opening a second alert (and sending a second email).
            open.Add(opened);

            logger.LogWarning("Alert opened [{Rule}] {Message}", candidate.RuleKey, candidate.Message);
        }

        foreach (var stale in open.Where(a => !candidates.Any(c => Same(a, c))))
        {
            stale.ResolvedAt = now;
            logger.LogInformation("Alert auto-resolved [{Rule}] {Message}", stale.RuleKey, stale.Message);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Alert>> GetOpenAlertsAsync(CancellationToken ct = default)
        => await db.Alerts
            .Include(a => a.Source)
            .Include(a => a.Event)
            .Where(a => a.ResolvedAt == null)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.TriggeredAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Alert>> GetRecentAlertsAsync(int take = 50, CancellationToken ct = default)
        => await db.Alerts
            .Include(a => a.Source)
            .Include(a => a.Event)
            .OrderByDescending(a => a.TriggeredAt)
            .Take(take)
            .ToListAsync(ct);
}
