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
    /// <summary>Watched, future events this source has an id for, ready to fetch.</summary>
    public async Task<IReadOnlyList<ExternalEventRef>> WatchlistRefsAsync(string sourceKey, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var events = await db.Watches
            .Where(w => w.Enabled && w.Event!.StartsAt > now)
            .Select(w => new { w.EventId, w.Event!.ExternalIds })
            .ToListAsync(ct);

        return events
            .Where(e => e.ExternalIds.ContainsKey(sourceKey))
            .Select(e => new ExternalEventRef(e.EventId, e.ExternalIds[sourceKey]))
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

            var result = await source.FetchPricesAsync(refs, ct);
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
        // Resolve every event in the payload once; a price fetch also refreshes status.
        var eventMap = new Dictionary<string, Event>();
        foreach (var canonical in result.Events)
            eventMap[canonical.SourceEventId] = await resolver.ResolveEventAsync(sourceRow.Key, canonical, ct);

        if (result.Observations.Count == 0)
            return 0;

        var candidates = new List<PriceObservation>();

        foreach (var o in result.Observations)
        {
            if (!eventMap.TryGetValue(o.SourceEventId, out var evt))
                continue;

            candidates.Add(new PriceObservation
            {
                EventId = evt.Id,
                SourceId = sourceRow.Id,
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

        // Events whose final price is on record are done with the stream.
        var finalised = await db.FinalPrices
            .Where(f => f.SourceId == sourceRow.Id && eventIds.Contains(f.EventId))
            .Select(f => f.EventId)
            .ToListAsync(ct);

        candidates.RemoveAll(c => finalised.Contains(c.EventId));

        var latest = await db.PriceObservations
            .Where(o => o.SourceId == sourceRow.Id && eventIds.Contains(o.EventId))
            .GroupBy(o => o.EventId)
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(ct);

        var lastSeen = latest.ToDictionary(o => o.EventId, o => (o.Lowest, o.Average, o.Highest, o.ListingCount, o.AllIn));
        var fresh = new List<PriceObservation>();

        foreach (var candidate in candidates)
        {
            var key = (candidate.Lowest, candidate.Average, candidate.Highest, candidate.ListingCount, candidate.AllIn);

            if (lastSeen.TryGetValue(candidate.EventId, out var previous) && previous == key)
                continue;

            fresh.Add(candidate);
            lastSeen[candidate.EventId] = key;
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
