using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// The pulls this platform can make, named individually. A job is a unit of work an operator
/// would ask for, and each declares what it costs before it runs. The scheduler runs these
/// same jobs, so automatic and manual paths cannot drift.
/// </summary>
public class IngestionJobs(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<IngestionJobs> logger)
{
    public const string EventsDiscover = "events:discover";
    public const string WatchlistPrices = "watchlist:prices";
    public const string OnSaleWatch = OnSaleWatchService.JobKey;
    public const string PricesFinalise = "prices:finalise";

    private readonly IngestionOptions _options = options.Value;

    public async Task<IReadOnlyList<IngestionJob>> DescribeAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var db = scope.ServiceProvider.GetRequiredService<TicketMiserDbContext>();

        var now = DateTimeOffset.UtcNow;
        var watchlist = await db.Watches.CountAsync(w => w.Enabled && w.Event!.StartsAt > now, ct);
        var priced = registry.PricedSources.Count;
        var discovery = registry.DiscoverySources.Count;

        return
        [
            new IngestionJob(
                EventsDiscover, "Find events",
                $"Every {_options.Market.City} {_options.Market.CategoryKey} inside {_options.Market.DiscoveryHorizon.TotalDays:F0} days, with on-sale dates.",
                EstimatedRequests: discovery * 8,
                Available: discovery > 0,
                Unavailable: discovery > 0 ? null : "No source registered. Set a Ticketmaster key or a SeatGeek client id."),

            new IngestionJob(
                WatchlistPrices, "Prices now",
                $"Current price summary for the {watchlist} watched events from every priced source.",
                EstimatedRequests: priced * watchlist,
                Available: priced > 0 && watchlist > 0,
                Unavailable: priced == 0 ? "No priced source registered." : watchlist == 0 ? "Nothing is watched." : null),

            new IngestionJob(
                OnSaleWatch, "On-sale tick",
                "One tick of the on-sale record for every watched event inside its window.",
                EstimatedRequests: priced,
                Available: priced > 0,
                Unavailable: priced > 0 ? null : "No priced source registered."),

            new IngestionJob(
                PricesFinalise, "Finalise started events",
                "Promotes the final price for events that have started, grades purchases, prunes the stream.",
                EstimatedRequests: 0,
                Available: true,
                Unavailable: null)
        ];
    }

    public async Task<JobOutcome> RunAsync(string key, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;

        try
        {
            var outcome = key switch
            {
                EventsDiscover => await RunDiscoveryAsync(ct),
                WatchlistPrices => await RunPricesAsync(ct),
                OnSaleWatch => await RunOnSaleAsync(ct),
                PricesFinalise => await RunFinaliseAsync(ct),
                _ => new JobOutcome(key, false, 0, 0, $"Unknown job '{key}'.")
            };

            logger.LogInformation("Job {Job}: {Rows} rows, {Failures} failures in {Elapsed:N0}ms",
                key, outcome.Rows, outcome.Failures, (DateTimeOffset.UtcNow - started).TotalMilliseconds);

            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {Job} failed", key);
            return new JobOutcome(key, false, 0, 1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<JobOutcome> RunDiscoveryAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var discovery = scope.ServiceProvider.GetRequiredService<DiscoveryService>();

        var rows = 0;
        var failures = 0;

        // The feed first, so the Discovery API sees events it already has ids for.
        foreach (var source in registry.DiscoverySources.OrderBy(s => s.Kind == SourceKind.Feed ? 0 : 1))
        {
            if (ct.IsCancellationRequested)
                break;

            var outcome = await discovery.DiscoverAsync(source, EventsDiscover, ct);
            rows += outcome.RowsIngested;
            if (outcome.Status == RunStatus.Failed)
                failures++;
        }

        return new JobOutcome(EventsDiscover, failures == 0, rows, failures, null);
    }

    private async Task<JobOutcome> RunPricesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var ingestion = scope.ServiceProvider.GetRequiredService<PriceIngestionService>();
        var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

        await initialiser.EnsurePartitionsAsync(ct);

        var rows = 0;
        var failures = 0;

        foreach (var source in registry.PricedSources)
        {
            if (ct.IsCancellationRequested)
                break;

            var refs = await ingestion.WatchlistRefsAsync(source, ct);
            var outcome = await ingestion.IngestAsync(source, WatchlistPrices, refs, ct);
            rows += outcome.RowsIngested;
            if (outcome.Status == RunStatus.Failed)
                failures++;
        }

        return new JobOutcome(WatchlistPrices, failures == 0, rows, failures,
            rows == 0 ? "No price moved since the last sweep." : $"{rows:N0} price {(rows == 1 ? "move" : "moves")}.");
    }

    private async Task<JobOutcome> RunOnSaleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var watch = scope.ServiceProvider.GetRequiredService<OnSaleWatchService>();
        var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

        await initialiser.EnsurePartitionsAsync(ct);
        var rows = await watch.TickAsync(ct);

        return new JobOutcome(OnSaleWatch, true, rows, 0, rows == 0 ? "No event is inside its on-sale window." : null);
    }

    private async Task<JobOutcome> RunFinaliseAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();

        var report = await retention.RunAsync(ct);
        await retention.DropEmptyPartitionsAsync(ct);

        return new JobOutcome(PricesFinalise, true, report.Promoted, 0,
            $"{report.Promoted} final prices, {report.Graded} purchases graded, {report.Pruned} observations pruned.");
    }
}

/// <summary>One named pull, and what it would cost.</summary>
public record IngestionJob(
    string Key,
    string Label,
    string Description,
    int EstimatedRequests,
    bool Available,
    string? Unavailable);

public record JobOutcome(string Key, bool Succeeded, int Rows, int Failures, string? Detail);
