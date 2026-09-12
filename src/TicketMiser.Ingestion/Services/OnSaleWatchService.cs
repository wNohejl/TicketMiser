using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Diagnostics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Writes the on-sale record: at every mark of a watched event's window, what each source
/// said. Every mark is written whether or not anything changed, because the timeline is the
/// product, and nothing here is ever pruned.
///
/// <para>
/// One run row per source per tick, so the budget guard and the reliability layer see the
/// spend as what it is: the expensive part of the day.
/// </para>
/// </summary>
public class OnSaleWatchService(
    TicketMiserDbContext db,
    SourceRegistry registry,
    CreditBudgetGuard budget,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<OnSaleWatchService> logger)
{
    private readonly OnSaleWatchOptions _settings = options.Value.OnSaleWatch;

    public const string JobKey = "onsale:watch";

    /// <summary>Watched events whose window contains <paramref name="now"/> and whose current mark is unrecorded.</summary>
    public async Task<IReadOnlyList<Event>> DueEventsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var earliest = now - _settings.Tail - _settings.HourlyTail;
        var latest = now + _settings.Lead;

        var candidates = await db.Watches
            .Where(w => w.Enabled && w.Event!.OnSaleAt != null
                        && w.Event.OnSaleAt >= earliest && w.Event.OnSaleAt <= latest)
            .Select(w => w.Event!)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return [];

        var ids = candidates.Select(e => e.Id).ToList();

        var lastTicks = await db.OnSaleTicks
            .Where(t => ids.Contains(t.EventId))
            .GroupBy(t => t.EventId)
            .Select(g => new { EventId = g.Key, Last = g.Max(t => t.ObservedAt) })
            .ToDictionaryAsync(x => x.EventId, x => x.Last, ct);

        return candidates
            .Where(e => OnSaleWindow.IsTickDue(e.OnSaleAt!.Value, lastTicks.GetValueOrDefault(e.Id), now, _settings))
            .ToList();
    }

    /// <summary>One tick over every due event. Returns rows written.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
            return 0;

        var now = clock.GetUtcNow();
        var due = await DueEventsAsync(now, ct);
        if (due.Count == 0)
            return 0;

        var written = 0;
        var ticksByEvent = new Dictionary<int, OnSaleTick>();

        foreach (var source in registry.PricedSources)
        {
            var keys = PriceIngestionService.KeysOf(source);

            var refs = due
                .SelectMany(e => keys.Where(e.ExternalIds.ContainsKey).Select(k => new ExternalEventRef(e.Id, e.ExternalIds[k])))
                .Distinct()
                .ToList();

            if (refs.Count == 0)
                continue;

            var sourceRow = await db.Sources.FirstAsync(s => s.Key == source.Key, ct);
            var rowsByKey = new Dictionary<string, Source> { [source.Key] = sourceRow };
            foreach (var key in keys.Where(k => k != source.Key))
                rowsByKey[key] = await db.Sources.FirstAsync(s => s.Key == key, ct);
            var run = new IngestionRun { SourceId = sourceRow.Id, JobKey = JobKey, StartedAt = now, Status = RunStatus.Running };
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                if (!await budget.TryReserveAsync(sourceRow, refs.Count, ct))
                {
                    run.Status = RunStatus.Partial;
                    run.Error = "Skipped: source is at its configured budget.";
                    run.FinishedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                var result = await source.FetchPricesAsync(refs, ct);
                run.RequestsMade = result.Cost.Requests;
                run.CreditsSpent = result.Cost.Credits;

                var byExternal = result.Observations.ToDictionary(o => o.SourceEventId);
                var channelByExternal = result.Events.ToDictionary(e => e.SourceEventId, e => e.Channel);

                foreach (var reference in refs)
                {
                    var evt = due.First(e => e.Id == reference.EventId);
                    byExternal.TryGetValue(reference.SourceEventId, out var o);

                    // The row this listing lives under: the adapter's own key unless the payload
                    // said marketplace, or unless the event only knows this id under the resale key.
                    var channel = channelByExternal.GetValueOrDefault(reference.SourceEventId,
                        evt.ExternalIds.TryGetValue(source.Key, out var primaryId) && primaryId == reference.SourceEventId
                            ? ListingChannel.Primary
                            : ListingChannel.Marketplace);
                    var row = rowsByKey[source.KeyFor(channel)];

                    var tick = new OnSaleTick
                    {
                        EventId = evt.Id,
                        SourceId = row.Id,
                        ObservedAt = now,
                        MinutesFromOnSale = (int)Math.Round((now - evt.OnSaleAt!.Value).TotalMinutes),
                        EventStatusCode = o?.EventStatusCode
                            ?? result.Events.FirstOrDefault(e => e.SourceEventId == reference.SourceEventId)?.Status,
                        Currency = o?.Currency ?? "USD",
                        Lowest = o?.Lowest,
                        Highest = o?.Highest,
                        ListingCount = o?.ListingCount,
                        AllIn = o?.AllIn,
                        IngestionRunId = run.Id
                    };

                    db.OnSaleTicks.Add(tick);
                    if (row.Kind == SourceKind.Primary)
                        ticksByEvent[evt.Id] = tick;

                    written++;
                }

                run.RowsIngested = refs.Count;
                run.Status = RunStatus.Success;
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);

                TicketMiserTelemetry.Instruments.OnSaleTicks.Add(refs.Count,
                    new KeyValuePair<string, object?>(TicketMiserTelemetry.Tags.Source, source.Key));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                run.Status = RunStatus.Failed;
                run.Error = $"{ex.GetType().Name}: {ex.Message}";
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                logger.LogError(ex, "{Source}: on-sale tick failed", source.Key);
            }
        }

        // Availability rides on the primary tick, one call for the whole list.
        foreach (var availability in registry.AvailabilitySources)
        {
            var primaryKey = TicketmasterKeyFor(availability.Key);
            var refs = due
                .Where(e => e.ExternalIds.ContainsKey(primaryKey))
                .Select(e => new ExternalEventRef(e.Id, e.ExternalIds[primaryKey]))
                .ToList();

            if (refs.Count == 0)
                continue;

            var sourceRow = await db.Sources.FirstAsync(s => s.Key == availability.Key, ct);
            var run = new IngestionRun { SourceId = sourceRow.Id, JobKey = JobKey, StartedAt = now, Status = RunStatus.Running };
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var result = await availability.FetchAvailabilityAsync(refs, ct);
                run.RequestsMade = result.Cost.Requests;

                foreach (var status in result.Statuses)
                {
                    var reference = refs.FirstOrDefault(r => r.SourceEventId == status.SourceEventId);
                    if (reference is null)
                        continue;

                    if (ticksByEvent.TryGetValue(reference.EventId, out var tick))
                    {
                        tick.PrimaryStatus = status.PrimaryStatus;
                        tick.ResaleStatus = status.ResaleStatus;
                    }
                    else
                    {
                        var evt = due.First(e => e.Id == reference.EventId);
                        db.OnSaleTicks.Add(new OnSaleTick
                        {
                            EventId = evt.Id,
                            SourceId = sourceRow.Id,
                            ObservedAt = now,
                            MinutesFromOnSale = (int)Math.Round((now - evt.OnSaleAt!.Value).TotalMinutes),
                            PrimaryStatus = status.PrimaryStatus,
                            ResaleStatus = status.ResaleStatus,
                            IngestionRunId = run.Id
                        });
                        written++;
                    }
                }

                run.RowsIngested = result.Statuses.Count;
                run.Status = result.Statuses.Count > 0 ? RunStatus.Success : RunStatus.Partial;
                run.Error = result.Statuses.Count > 0 ? null : "Provider returned no statuses.";
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                run.Status = RunStatus.Failed;
                run.Error = $"{ex.GetType().Name}: {ex.Message}";
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                logger.LogError(ex, "{Source}: availability tick failed", availability.Key);
            }
        }

        if (written > 0)
            logger.LogInformation("On-sale tick: {Events} events, {Rows} rows", due.Count, written);

        return written;
    }

    /// <summary>The Inventory Status API answers for Ticketmaster ids; its own key is a separate budget.</summary>
    private static string TicketmasterKeyFor(string availabilityKey)
        => availabilityKey == "ticketmaster-inventory" ? "ticketmaster" : availabilityKey;
}
