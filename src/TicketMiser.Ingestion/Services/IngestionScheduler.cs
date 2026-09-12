using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Drives unattended ingestion. Four jobs, each triggered by the thing that makes it due:
/// discovery daily, the on-sale tick whenever a watched event is inside its window and its
/// current mark is unrecorded, the progression sweep on the derived cadence (only when the
/// operator has switched unattended spending on), and retention on its interval.
///
/// <para>
/// The loop ticks on a short interval and asks what is due rather than sleeping until the
/// next job, so a restart never skips a window. The clock is injected so a test can run a
/// whole on-sale window in a second.
/// </para>
/// </summary>
public class IngestionScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<IngestionScheduler> logger) : BackgroundService
{
    private readonly IngestionOptions _options = options.Value;

    private DateTimeOffset? _lastDiscovery;
    private DateTimeOffset? _lastSweep;
    private DateTimeOffset? _lastRetention;
    private TimeSpan _sweepInterval = TimeSpan.FromMinutes(30);

    /// <summary>The cadence currently in force, for the Ops surfaces.</summary>
    public PollPlan? CurrentPlan { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ingestion scheduler started");

        // Startup runs discovery, which is cheap, and never the price jobs, which are not.
        if (_options.RunOnStartup)
        {
            await RunJobAsync(IngestionJobs.EventsDiscover, stoppingToken);
            _lastDiscovery = clock.GetUtcNow();
        }

        using var timer = new PeriodicTimer(_options.TickInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed; continuing");
            }
        }

        logger.LogInformation("Ingestion scheduler stopped");
    }

    /// <summary>One pass over everything that might be due. Public so the cadence test can drive it.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        if (_options.Retention.Enabled && Due(_lastRetention, _options.Retention.Interval, now))
        {
            await RunJobAsync(IngestionJobs.PricesFinalise, ct);
            _lastRetention = now;
        }

        if (Due(_lastDiscovery, _options.Discovery.Interval, now))
        {
            await RunJobAsync(IngestionJobs.EventsDiscover, ct);
            _lastDiscovery = now;
        }

        // The on-sale watch decides for itself whether a mark is owed; the tick is cheap when
        // nothing is inside a window. A watch is the operator's ask, so this runs unattended.
        if (_options.OnSaleWatch.Enabled)
            await RunJobAsync(IngestionJobs.OnSaleWatch, ct);

        if (_options.PricePolling.RunsUnattended && Due(_lastSweep, _sweepInterval, now))
        {
            await SweepAsync(ct);
            _lastSweep = now;
        }
    }

    private static bool Due(DateTimeOffset? last, TimeSpan every, DateTimeOffset now)
        => last is null || now - last >= every;

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var planner = scope.ServiceProvider.GetRequiredService<PricePollPlanner>();

        var plan = await planner.PlanAsync(registry.PricedSources.Select(s => s.Key).ToList(), ct);
        if (plan is null)
            return;

        CurrentPlan = plan;
        _sweepInterval = plan.Interval;

        if (plan.Watchlist == 0)
            return;

        logger.LogInformation("Sweep: {Calls} calls, next in {Interval}{Bound}",
            plan.CallsPerSweep, plan.Interval, plan.BoundBy is null ? "" : $", paced by {plan.BoundBy}");

        await RunJobAsync(IngestionJobs.WatchlistPrices, ct);
    }

    private async Task RunJobAsync(string key, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IngestionJobs>();
        await jobs.RunAsync(key, ct);
    }
}
