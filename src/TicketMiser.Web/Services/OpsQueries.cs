using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;
using TicketMiser.Reliability;

namespace TicketMiser.Web.Services;

/// <summary>
/// One source's daily allowance, as the budget guard and the planner both see it.
///
/// <para>
/// <see cref="Reserved"/> is the planner's reserve: the slice of the daily ceiling held back
/// from the pacing maths so a manual pull or an on-sale watch is never refused. It is
/// recomputed here with the same arithmetic as <c>PricePollPlanner</c>, because the point of
/// showing it is that the number on screen is the number the scheduler is using.
/// </para>
/// </summary>
public record SourceBudget(
    Source Source,
    BudgetUsage Usage,
    int? DailyLimit,
    int UsedToday,
    int Reserved,
    int Left);

/// <summary>Everything the Ops window shows, read in one pass so the numbers agree with each other.</summary>
public record OpsSnapshot(
    IReadOnlyList<SourceHealth> Health,
    IReadOnlyList<SourceBudget> Budgets,
    PollPlan? Plan,
    bool SweepsUnattended,
    DateTimeOffset? LastSweepAt,
    int CallsPerOnSaleEvent,
    IReadOnlyList<Alert> Alerts,
    int OpenIncidents,
    long RowsToday,
    TimeSpan FreshnessSlo,
    double SuccessRateSlo)
{
    public static OpsSnapshot Empty { get; } = new(
        [], [], null, false, null, 0, [], 0, 0, TimeSpan.FromHours(26), 0.95);

    /// <summary>
    /// The provider whose allowance governs the cadence: the one the planner named, or
    /// failing that the one under the most pressure. Null when nothing is metered.
    /// </summary>
    public SourceBudget? Governing
        => Budgets.FirstOrDefault(b => b.Source.Key == Plan?.BoundBy)
           ?? Budgets.Where(b => !b.Usage.IsUnmetered).MaxBy(b => b.Usage.WorstUtilisation);

    /// <summary>
    /// When the next unattended sweep is due, or null when sweeps run on request only. A
    /// schedule that has never swept is due at its next tick, which is now for the purposes
    /// of a clock on screen.
    /// </summary>
    public DateTimeOffset? NextSweepAt
        => !SweepsUnattended || Plan is null ? null
            : LastSweepAt is { } last ? last + Plan.Interval
            : DateTimeOffset.UtcNow;
}

/// <summary>What the Ops panel reads and does. An interface so a render test can hand the panel a snapshot.</summary>
public interface IOpsQueries
{
    Task<OpsSnapshot> LoadAsync(CancellationToken ct = default);

    /// <summary>Rolls the KPIs up and re-runs the alert rules.</summary>
    Task EvaluateAsync(CancellationToken ct = default);

    /// <summary>Opens an incident from an alert and returns its id.</summary>
    Task<int> PromoteAsync(long alertId, CancellationToken ct = default);
}

/// <summary>
/// The Ops panel's reads, against the reliability layer and the planner.
///
/// A scope per call rather than a scoped DbContext: a panel lives as long as its circuit,
/// and a context that lives that long grows a tracking graph and stale reads with it.
/// </summary>
public sealed class OpsQueries(
    IServiceScopeFactory scopeFactory,
    IOptions<ReliabilityOptions> reliability,
    IOptions<IngestionOptions> ingestion) : IOpsQueries
{
    public async Task<OpsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<TicketMiserDbContext>();
        var kpi = provider.GetRequiredService<KpiCalculator>();
        var budget = provider.GetRequiredService<BudgetCalculator>();
        var alerts = provider.GetRequiredService<AlertEngine>();
        var registry = provider.GetRequiredService<SourceRegistry>();
        var planner = provider.GetRequiredService<PricePollPlanner>();

        var o = reliability.Value;
        var polling = ingestion.Value.PricePolling;

        var health = await kpi.GetAllHealthAsync(o.SuccessRateWindow, o.VolumeBaselineDays, ct);

        // Recomputed rather than read off the scheduler: the plan is a pure function of the
        // budget already spent, so the panel and the scheduler reach the same answer without
        // the panel holding a handle on a background service.
        var plan = await planner.PlanAsync(registry.PricedSources.Select(s => s.Key).ToList(), ct);

        var budgets = new List<SourceBudget>(health.Count);

        foreach (var h in health)
        {
            var usage = await budget.GetUsageAsync(h.Source, ct);
            var daily = h.Source.RateLimitPerDay;

            // The planner's own reserve arithmetic, so the two never disagree on screen.
            var reserved = daily is { } d ? (int)Math.Ceiling(d * polling.ReservePercent / 100d) : 0;
            var left = daily is { } limit ? Math.Max(0, limit - reserved - usage.RequestsLastDay) : 0;

            budgets.Add(new SourceBudget(h.Source, usage, daily, usage.RequestsLastDay, reserved, left));
        }

        var lastSweep = await db.IngestionRuns
            .Where(r => r.JobKey == IngestionJobs.WatchlistPrices)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .FirstOrDefaultAsync(ct);

        var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var rowsToday = await db.IngestionRuns
            .Where(r => r.StartedAt >= dayStart)
            .SumAsync(r => (long?)r.RowsIngested, ct) ?? 0;

        return new OpsSnapshot(
            Health: health,
            Budgets: budgets,
            Plan: plan,
            SweepsUnattended: polling.RunsUnattended,
            LastSweepAt: lastSweep,
            CallsPerOnSaleEvent: OnSaleWindow.CallsPerEvent(ingestion.Value.OnSaleWatch),
            Alerts: await alerts.GetOpenAlertsAsync(ct),
            OpenIncidents: await db.Incidents.CountAsync(i => i.Status != IncidentStatus.Resolved, ct),
            RowsToday: rowsToday,
            FreshnessSlo: o.FreshnessSlo,
            SuccessRateSlo: o.SuccessRateSlo);
    }

    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KpiCalculator>().RollupDailyAsync(ct);
        await scope.ServiceProvider.GetRequiredService<AlertEngine>().EvaluateAsync(ct);
    }

    public async Task<int> PromoteAsync(long alertId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var incident = await scope.ServiceProvider.GetRequiredService<IncidentService>().PromoteAsync(alertId, ct: ct);
        return incident.Id;
    }
}
