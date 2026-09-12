using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Diagnostics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

public record IngestionOutcome(long RunId, RunStatus Status, int RowsIngested, string? Error);

/// <summary>
/// Sweeps one source for events in the configured market and resolves them, recording the
/// on-sale times that the on-sale watch will later schedule from. Every execution writes an
/// <see cref="IngestionRun"/> whether it succeeds or fails; that table is the raw feed the
/// reliability layer is computed from.
/// </summary>
public class DiscoveryService(
    TicketMiserDbContext db,
    EventResolver resolver,
    CreditBudgetGuard budget,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<DiscoveryService> logger)
{
    private readonly IngestionOptions _options = options.Value;

    public async Task<IngestionOutcome> DiscoverAsync(IPriceSource source, string jobKey, CancellationToken ct)
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

        using var activity = TicketMiserTelemetry.Source.StartActivity("ingest.discover");
        activity?.SetTag(TicketMiserTelemetry.Tags.Source, source.Key);
        activity?.SetTag(TicketMiserTelemetry.Tags.Job, jobKey);

        try
        {
            // A Ticketmaster sweep is a handful of pages; a feed download is one request, and
            // the feed's ceiling is a handful a day, so estimating pages for it refused every run.
            var estimated = source.Kind == SourceKind.Feed ? 1 : 8;

            if (!await budget.TryReserveAsync(sourceRow, estimated, ct))
            {
                run.Status = RunStatus.Partial;
                run.Error = "Skipped: source is at its configured budget.";
                run.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                return Complete(activity, run, source.Key, rows: 0);
            }

            if (source is IFailureInjectable injectable)
                injectable.FailureMode = sourceRow.FailureMode;

            var now = clock.GetUtcNow();
            var market = _options.Market;
            var query = new DiscoveryQuery(
                market.City, market.StateCode, market.CountryCode, market.CategoryKey,
                From: now, To: now + market.DiscoveryHorizon);

            var result = await source.DiscoverAsync(query, ct);
            run.RequestsMade = result.Cost.Requests;
            run.CreditsSpent = result.Cost.Credits;

            var rows = 0;
            foreach (var canonical in result.Events)
            {
                ct.ThrowIfCancellationRequested();

                // A marketplace listing is recorded under the source's resale key, so the id,
                // the runs and the prices for it never read as the primary market's.
                await resolver.ResolveEventAsync(source.KeyFor(canonical.Channel), canonical, ct);
                rows++;
            }

            run.RowsIngested = rows;
            run.FinishedAt = clock.GetUtcNow();

            // Two different zeroes: no events at all is the signature of a broken or
            // misconfigured sweep, not of a quiet town.
            if (result.Events.Count == 0)
            {
                run.Status = RunStatus.Partial;
                run.Error = "Provider returned no events for the market; possible schema drift or wrong filters.";
            }
            else
            {
                run.Status = RunStatus.Success;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("{Source}: discovery resolved {Rows} events", source.Key, rows);
            return Complete(activity, run, source.Key, rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Status = RunStatus.Failed;
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            activity?.AddException(ex);
            logger.LogError(ex, "{Source}: discovery failed", source.Key);
            return Complete(activity, run, source.Key, rows: 0);
        }
    }

    internal static IngestionOutcome Complete(Activity? activity, IngestionRun run, string sourceKey, int rows)
    {
        var tags = new TagList
        {
            { TicketMiserTelemetry.Tags.Source, sourceKey },
            { TicketMiserTelemetry.Tags.Job, run.JobKey },
            { TicketMiserTelemetry.Tags.Status, run.Status.ToString() }
        };

        TicketMiserTelemetry.Instruments.Runs.Add(1, tags);
        TicketMiserTelemetry.Instruments.Rows.Add(rows, tags);
        TicketMiserTelemetry.Instruments.Requests.Add(run.RequestsMade, tags);
        TicketMiserTelemetry.Instruments.Credits.Add(run.CreditsSpent, tags);

        activity?.SetTag(TicketMiserTelemetry.Tags.Status, run.Status.ToString());
        activity?.SetStatus(
            run.Status is RunStatus.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, run.Error);

        return new IngestionOutcome(run.Id, run.Status, rows, run.Error);
    }
}
