using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Diagnostics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Fetches one source's current prices for the watchlist and persists what changed.
///
/// <para>
/// Store on change, not on poll. A summary that has not moved since the last observation
/// carries no information, so re-running a window is a no-op rather than a pile of duplicate
/// rows, and every row in the table is a real move.
/// </para>
/// </summary>
public class PriceIngestionService(
    TicketMiserDbContext db,
    EventResolver resolver,
    CreditBudgetGuard budget,
    TimeProvider clock,
    ILogger<PriceIngestionService> logger)
{
    /// <summary>The source keys an adapter answers for: its own, and its marketplace key when it has one.</summary>
    public static IReadOnlyList<string> KeysOf(IPriceSource source)
        => new[] { source.Key, source.KeyFor(ListingChannel.Marketplace) }.Distinct().ToList();

    /// <summary>
    /// Watched, future events this adapter has an id for under any of its keys, ready to fetch.
    /// The union across owners, so an event two people watch is one ref and one call.
    /// </summary>
    public async Task<IReadOnlyList<ExternalEventRef>> WatchlistRefsAsync(IPriceSource source, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var keys = KeysOf(source);

        var events = await db.WatchedEvents()
            .Where(e => e.StartsAt > now)
            .Select(e => new { EventId = e.Id, e.ExternalIds })
            .ToListAsync(ct);

        return events
            .SelectMany(e => keys.Where(e.ExternalIds.ContainsKey).Select(k => new ExternalEventRef(e.EventId, e.ExternalIds[k])))
            .Distinct()
            .ToList();
    }

    public async Task<IngestionOutcome> IngestAsync(
        IPriceSource source, string jobKey, IReadOnlyList<ExternalEventRef> refs, CancellationToken ct)
    {
        var sourceRow = await db.Sources.FirstOrDefaultAsync(s => s.Key == source.Key, ct)
            ?? throw new InvalidOperationException($"Source '{source.Key}' is not registered.");

        var run = new IngestionRun
        {
            SourceId = sourceRow.Id,
            JobKey = jobKey,
            StartedAt = clock.GetUtcNow(),
            Status = RunStatus.Running
        };

        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        using var activity = TicketMiserTelemetry.Source.StartActivity("ingest.prices");
        activity?.SetTag(TicketMiserTelemetry.Tags.Source, source.Key);
        activity?.SetTag(TicketMiserTelemetry.Tags.Job, jobKey);

        try
        {
            if (refs.Count == 0)
            {
                run.Status = RunStatus.Success;
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                return DiscoveryService.Complete(activity, run, source.Key, rows: 0);
            }

            // Refuse to start rather than blow the ceiling mid-run. One call per event.
            if (!await budget.TryReserveAsync(sourceRow, estimatedRequests: refs.Count, ct))
            {
                run.Status = RunStatus.Partial;
                run.Error = "Skipped: source is at its configured budget.";
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                logger.LogWarning("{Source}: skipped, budget exhausted", source.Key);
                return DiscoveryService.Complete(activity, run, source.Key, rows: 0);
            }

            if (source is IFailureInjectable injectable)
                injectable.FailureMode = sourceRow.FailureMode;

            var result = (await source.FetchPricesAsync(refs, ct)) with { Source = source };
            run.RequestsMade = result.Cost.Requests;
            run.CreditsSpent = result.Cost.Credits;

            var rows = await PersistAsync(sourceRow, result, run.Id, ct);

            run.RowsIngested = rows;
            run.FinishedAt = clock.GetUtcNow();

            // The provider answered for none of the events we asked about: the silent failure.
            if (result.Observations.Count == 0 && result.Events.Count == 0)
            {
                run.Status = RunStatus.Partial;
                run.Error = "Provider returned no prices; possible upstream schema drift.";
            }
            else
            {
                run.Status = RunStatus.Success;
            }

            await db.SaveChangesAsync(ct);
            return DiscoveryService.Complete(activity, run, source.Key, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Failed;
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            activity?.AddException(ex);
            logger.LogError(ex, "{Source}: price ingestion failed", source.Key);
            return DiscoveryService.Complete(activity, run, source.Key, rows: 0);
        }
    }

    private async Task<int> PersistAsync(Source sourceRow, PriceFetchResult result, long runId, CancellationToken ct)
    {
        // Resolve every event in the payload once; a price fetch also refreshes status. A
        // marketplace listing resolves and is priced under the resale source row.
        var eventMap = new Dictionary<string, (Event Event, Source Row)>();
        var rowsByKey = new Dictionary<string, Source> { [sourceRow.Key] = sourceRow };

        foreach (var canonical in result.Events)
        {
            var key = result.Source.KeyFor(canonical.Channel);
            if (!rowsByKey.TryGetValue(key, out var row))
            {
                row = await db.Sources.FirstOrDefaultAsync(s => s.Key == key, ct)
                      ?? throw new InvalidOperationException($"Source '{key}' is not registered.");
                rowsByKey[key] = row;
            }

            eventMap[canonical.SourceEventId] = (await resolver.ResolveEventAsync(key, canonical, ct), row);
        }

        if (result.Observations.Count == 0)
            return 0;

        var candidates = new List<PriceObservation>();

        foreach (var o in result.Observations)
        {
            if (!eventMap.TryGetValue(o.SourceEventId, out var entry))
                continue;

            var (evt, row) = entry;

            candidates.Add(new PriceObservation
            {
                EventId = evt.Id,
                SourceId = row.Id,
                ObservedAt = o.ObservedAt,
                Currency = o.Currency,
                Lowest = o.Lowest,
                Average = o.Average,
                Highest = o.Highest,
                ListingCount = o.ListingCount,
                AllIn = o.AllIn,
                FaceMin = o.FaceMin,
                FaceMax = o.FaceMax,
                IngestionRunId = runId
            });
        }

        if (candidates.Count == 0)
            return 0;

        var eventIds = candidates.Select(c => c.EventId).Distinct().ToList();
        var sourceIds = rowsByKey.Values.Select(r => r.Id).ToList();

        // Events whose final price is on record are done with the stream.
        var finalised = await db.FinalPrices
            .Where(f => sourceIds.Contains(f.SourceId) && eventIds.Contains(f.EventId))
            .Select(f => new { f.EventId, f.SourceId })
            .ToListAsync(ct);

        candidates.RemoveAll(c => finalised.Any(f => f.EventId == c.EventId && f.SourceId == c.SourceId));

        var latest = await db.PriceObservations
            .Where(o => sourceIds.Contains(o.SourceId) && eventIds.Contains(o.EventId))
            .GroupBy(o => new { o.EventId, o.SourceId })
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(ct);

        var lastSeen = latest.ToDictionary(
            o => (o.EventId, o.SourceId),
            o => (o.Lowest, o.Average, o.Highest, o.ListingCount, o.AllIn));
        var fresh = new List<PriceObservation>();

        foreach (var candidate in candidates)
        {
            var identity = (candidate.EventId, candidate.SourceId);
            var key = (candidate.Lowest, candidate.Average, candidate.Highest, candidate.ListingCount, candidate.AllIn);

            if (lastSeen.TryGetValue(identity, out var previous) && previous == key)
                continue;

            fresh.Add(candidate);
            lastSeen[identity] = key;
        }

        if (fresh.Count == 0)
        {
            logger.LogInformation("{Source}: no price movement since last poll", sourceRow.Key);
            return 0;
        }

        db.PriceObservations.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }
}
