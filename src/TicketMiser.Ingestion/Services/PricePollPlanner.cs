using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Decides how often to sweep the watchlist, from the budget rather than the clock.
///
/// <para>
/// Take what each provider allows in a day, subtract the reserve and what is already spent,
/// set aside what today's on-sale windows will cost, divide the rest by the cost of one sweep
/// (one call per watched event), and spread the sweeps over the hours that remain. The
/// tightest provider governs.
/// </para>
/// </summary>
public class PricePollPlanner(
    TicketMiserDbContext db,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<PricePollPlanner> logger)
{
    private readonly IngestionOptions _options = options.Value;

    public async Task<PollPlan?> PlanAsync(IReadOnlyList<string> sourceKeys, CancellationToken ct = default)
    {
        if (sourceKeys.Count == 0)
            return null;

        var now = clock.GetUtcNow();
        var settings = _options.PricePolling;

        var watchlist = await db.Watches.CountAsync(w => w.Enabled && w.Event!.StartsAt > now, ct);
        var perSweep = Math.Max(1, watchlist);

        var dayEnd = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        var onSaleToday = await db.Watches
            .CountAsync(w => w.Enabled && w.Event!.OnSaleAt != null && w.Event.OnSaleAt >= now && w.Event.OnSaleAt < dayEnd, ct);
        var onSaleCost = onSaleToday * OnSaleWindow.CallsPerEvent(_options.OnSaleWatch);

        var sources = await db.Sources.Where(s => sourceKeys.Contains(s.Key)).AsNoTracking().ToListAsync(ct);

        TimeSpan? tightest = null;
        string? binding = null;
        var sweepsLeft = int.MaxValue;
        var reservedCalls = 0;

        foreach (var source in sources)
        {
            var plan = await PlanForAsync(source, perSweep, onSaleCost, settings, now, ct);
            if (plan is null)
                continue;

            if (tightest is null || plan.Value.Interval > tightest)
            {
                tightest = plan.Value.Interval;
                binding = source.Key;
                reservedCalls = plan.Value.Reserve;
            }

            sweepsLeft = Math.Min(sweepsLeft, plan.Value.SweepsRemaining);
        }

        var interval = Clamp(tightest ?? settings.MinimumInterval, settings);

        return new PollPlan(
            Interval: interval,
            CallsPerSweep: perSweep,
            SweepsRemainingToday: sweepsLeft == int.MaxValue ? null : sweepsLeft,
            BoundBy: binding,
            Watchlist: watchlist,
            OnSaleEventsToday: onSaleToday,
            OnSaleCallsReserved: onSaleCost,
            ReserveCalls: reservedCalls);
    }

    private async Task<(TimeSpan Interval, int SweepsRemaining, int Reserve)?> PlanForAsync(
        Source source, int perSweep, int onSaleCost, PricePollingOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        int? fromDaily = null;
        var reserve = 0;

        if (source.RateLimitPerDay is { } daily)
        {
            var used = await db.IngestionRuns
                .Where(r => r.SourceId == source.Id && r.StartedAt >= now.AddDays(-1))
                .SumAsync(r => (int?)r.RequestsMade, ct) ?? 0;

            reserve = (int)Math.Ceiling(daily * settings.ReservePercent / 100d);
            var usable = Math.Max(0, daily - reserve - used - onSaleCost);
            fromDaily = usable / perSweep;
        }

        int? fromHourly = null;

        if (source.RateLimitPerHour is { } hourly)
        {
            var hourlyReserve = (int)Math.Ceiling(hourly * settings.ReservePercent / 100d);
            fromHourly = Math.Max(0, hourly - hourlyReserve) / perSweep * 24;
        }

        if (fromDaily is null && fromHourly is null)
            return null;

        var sweeps = Math.Min(fromDaily ?? int.MaxValue, fromHourly ?? int.MaxValue);

        if (sweeps <= 0)
        {
            logger.LogWarning("{Source}: price budget spent for today; backing off", source.Key);
            return (settings.MaximumInterval, 0, reserve);
        }

        var hoursLeft = Math.Max(1d, (new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1) - now).TotalHours);
        return (TimeSpan.FromHours(hoursLeft / sweeps), sweeps, reserve);
    }

    private static TimeSpan Clamp(TimeSpan interval, PricePollingOptions settings)
    {
        if (interval < settings.MinimumInterval)
            return settings.MinimumInterval;

        return interval > settings.MaximumInterval ? settings.MaximumInterval : interval;
    }
}

/// <summary>The chosen cadence, and enough context to explain it on screen.</summary>
public record PollPlan(
    TimeSpan Interval,
    int CallsPerSweep,
    int? SweepsRemainingToday,
    string? BoundBy,
    int Watchlist,
    int OnSaleEventsToday,
    int OnSaleCallsReserved,
    int ReserveCalls);
