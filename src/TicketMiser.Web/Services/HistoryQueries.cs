using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>How much of the past the record holds: events that have started, and how many of them have a day-of price.</summary>
public record HistorySummary(int PastEvents, int EventsWithFinal, int FinalRows);

/// <summary>What the History panel reads. An interface so a render test can stand in for the database.</summary>
public interface IHistoryQueries
{
    Task<HistorySummary> SummaryAsync(CancellationToken ct = default);

    /// <summary>The newest final prices, with their event, venue and source attached.</summary>
    Task<IReadOnlyList<FinalPrice>> RecentFinalsAsync(int take, CancellationToken ct = default);
}

public sealed class HistoryQueries(IDbContextFactory<TicketMiserDbContext> dbFactory) : IHistoryQueries
{
    public async Task<HistorySummary> SummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var pastEvents = await db.Events.CountAsync(e => e.StartsAt <= now, ct);
        var withFinal = await db.FinalPrices.Select(f => f.EventId).Distinct().CountAsync(ct);
        var rows = await db.FinalPrices.CountAsync(ct);

        return new HistorySummary(pastEvents, withFinal, rows);
    }

    public async Task<IReadOnlyList<FinalPrice>> RecentFinalsAsync(int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.FinalPrices
            .Include(f => f.Event).ThenInclude(e => e!.Venue)
            .Include(f => f.Source)
            .AsNoTracking()
            .OrderByDescending(f => f.Event!.StartsAt)
            .ThenBy(f => f.Source!.Name)
            .Take(take)
            .ToListAsync(ct);
    }
}
