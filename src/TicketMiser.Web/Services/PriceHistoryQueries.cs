using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Every source's latest word on one event, for the All sources follow-up. Primary first, then
/// resale, each by source name: listed, never ranked, because the window exists to show every
/// number beside its source rather than to pick one.
/// </summary>
public sealed record EventQuotes(Event Event, IReadOnlyList<SourceQuote> Quotes)
{
    public IReadOnlyList<SourceQuote> Primary => Quotes.Where(q => q.Kind == SourceKind.Primary).ToList();

    public IReadOnlyList<SourceQuote> Resale => Quotes.Where(q => q.Kind == SourceKind.Resale).ToList();
}

/// <summary>What the Price history window, the Event window's history tab, Trend and All sources read.</summary>
public interface IPriceHistoryQueries
{
    /// <summary>
    /// The events a history can be picked from when the window opens about nothing: every event
    /// with an enabled watch, including those that started in the last
    /// <see cref="PriceHistoryQueries.RecentDays"/> days, soonest first.
    /// </summary>
    Task<IReadOnlyList<Event>> ChoicesAsync(CancellationToken ct = default);

    /// <summary>The event's history, per market and basis, with its purchases. Null when no event has the id.</summary>
    Task<PriceHistory?> LoadAsync(int eventId, CancellationToken ct = default);

    /// <summary>The last <see cref="PriceTrend.DefaultDays"/> days of the history, per market. Null when no event has the id.</summary>
    Task<PriceTrend?> TrendAsync(int eventId, CancellationToken ct = default);

    /// <summary>Every source's latest quote on the event. Null when no event has the id.</summary>
    Task<EventQuotes?> QuotesAsync(int eventId, CancellationToken ct = default);
}

/// <summary>
/// The reads behind one event's prices over time. The composition is in Core
/// (<see cref="PriceHistory.Compose"/>, <see cref="PriceTrend.Compose"/>,
/// <see cref="LatestQuotes.For"/>) so it can be pinned without a database; this class only
/// fetches the rows. A context per call, like every desk query.
/// </summary>
public sealed class PriceHistoryQueries(IDbContextFactory<TicketMiserDbContext> factory, TimeProvider clock, IOwnerContext owners) : IPriceHistoryQueries
{
    /// <summary>How far back a started event stays pickable: long enough to read how its prices ended.</summary>
    public const int RecentDays = 30;

    public async Task<IReadOnlyList<Event>> ChoicesAsync(CancellationToken ct = default)
    {
        var owner = await owners.GetAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var since = clock.GetUtcNow().AddDays(-RecentDays);
        var watches = db.Watches.VisibleTo(owner);

        return await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Where(e => e.StartsAt >= since && watches.Any(w => w.Enabled && w.EventId == e.Id))
            .OrderBy(e => e.StartsAt)
            .ToListAsync(ct);
    }

    public async Task<PriceHistory?> LoadAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ReadAsync(db, eventId, ct);
    }

    public async Task<PriceTrend?> TrendAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await ReadAsync(db, eventId, ct) is { } history
            ? PriceTrend.Compose(history, clock.GetUtcNow())
            : null;
    }

    public async Task<EventQuotes?> QuotesAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (await EventAsync(db, eventId, ct) is not { } evt)
            return null;

        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);

        var observations = await db.PriceObservations
            .AsNoTracking()
            .Where(o => o.EventId == eventId)
            .GroupBy(o => o.SourceId)
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(ct);

        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => t.EventId == eventId)
            .GroupBy(t => t.SourceId)
            .Select(g => g.OrderByDescending(t => t.ObservedAt).First())
            .ToListAsync(ct);

        return new EventQuotes(evt, LatestQuotes.For(eventId, observations, ticks, sources));
    }

    private static Task<Event?> EventAsync(TicketMiserDbContext db, int eventId, CancellationToken ct)
        => db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    private async Task<PriceHistory?> ReadAsync(TicketMiserDbContext db, int eventId, CancellationToken ct)
    {
        if (await EventAsync(db, eventId, ct) is not { } evt)
            return null;

        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);

        var observations = await db.PriceObservations
            .AsNoTracking()
            .Where(o => o.EventId == eventId && o.Lowest != null)
            .ToListAsync(ct);

        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => t.EventId == eventId && t.Lowest != null)
            .ToListAsync(ct);

        var finals = await db.FinalPrices
            .AsNoTracking()
            .Where(f => f.EventId == eventId)
            .ToListAsync(ct);

        // Read, never written: the history marks what was paid and leaves the grading to Purchases.
        // The reader's own purchases only; another owner's are not theirs to see.
        var purchases = await db.Purchases
            .AsNoTracking()
            .VisibleTo(await owners.GetAsync(ct))
            .Where(p => p.EventId == eventId)
            .ToListAsync(ct);

        return PriceHistory.Compose(evt, observations, ticks, finals, purchases, sources);
    }
}
