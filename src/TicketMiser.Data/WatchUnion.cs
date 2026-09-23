using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;

namespace TicketMiser.Data;

/// <summary>
/// What the scheduler polls: the union of enabled watches across every owner, as events. Two
/// owners watching one event are one event here, so they cost one fetch, one on-sale window
/// and one line of the budget — the scheduler never reads <see cref="Watch"/> rows directly.
/// </summary>
public static class WatchUnion
{
    /// <summary>Every event at least one owner (the operator included) has an enabled watch on.</summary>
    public static IQueryable<Event> WatchedEvents(this TicketMiserDbContext db)
        => db.Events.Where(e => db.Watches.Any(w => w.Enabled && w.EventId == e.Id));

    /// <summary>
    /// The union's size for the Ops window and the planner: distinct watched events that have
    /// not started, the watch rows behind them, and how many owners hold those rows (the
    /// operator counts as one).
    /// </summary>
    public static async Task<WatchUnionSize> SizeAsync(this TicketMiserDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var live = db.Watches.Where(w => w.Enabled && w.Event!.StartsAt > now);

        var events = await live.Select(w => w.EventId).Distinct().CountAsync(ct);
        var watches = await live.CountAsync(ct);
        var owners = await live.Select(w => w.OwnerId).Distinct().CountAsync(ct);

        return new WatchUnionSize(events, watches, owners);
    }
}

/// <param name="Events">Distinct watched events: what a sweep costs, one call each.</param>
/// <param name="Watches">Enabled watch rows behind them.</param>
/// <param name="Owners">Distinct owners of those rows; the operator's unowned rows count as one.</param>
public readonly record struct WatchUnionSize(int Events, int Watches, int Owners);
