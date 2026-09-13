using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>Where an event's public sale stands against the clock.</summary>
public enum OnSaleState
{
    /// <summary>No on-sale time has been named, or the source says it is to be announced.</summary>
    Unannounced,

    /// <summary>The on-sale time is in the future.</summary>
    Upcoming,

    /// <summary>Inside the on-sale window the record is kept over: T to T+120 minutes.</summary>
    OnSaleNow,

    /// <summary>The window has passed; the sale opened some time ago.</summary>
    Opened
}

/// <summary>
/// One watched event on the board: the event, where its sale stands, the best number in each
/// market with that market's rail, what the primary market last said about availability, and
/// the price the operator asked to hear about.
/// </summary>
/// <param name="Primary">Built from primary quotes only.</param>
/// <param name="Resale">Built from resale quotes only. The two are never compared.</param>
/// <param name="PrimaryStatus">The latest primary tick's inventory status, as the source spelled it. Null when no primary tick exists.</param>
/// <param name="PrimaryReappeared">Primary reported tickets again after having reported none.</param>
public sealed record WatchlistRow(
    Watch Watch,
    Event Event,
    OnSaleState OnSaleState,
    MarketBest Primary,
    MarketBest Resale,
    string? PrimaryStatus,
    DateTimeOffset? PrimaryStatusAt,
    bool PrimaryReappeared)
{
    public decimal? TargetPrice => Watch.TargetPrice;

    /// <summary>Whether any source has said anything about this event.</summary>
    public bool Reported => Primary.Reported || Resale.Reported;

    /// <summary>
    /// Where the sale stands, from the event's own on-sale time and the clock. Pure, so a
    /// render test can pin each state without a database.
    /// </summary>
    public static OnSaleState StateOf(Event evt, DateTimeOffset now)
    {
        if (evt.OnSaleTbd || evt.OnSaleAt is not { } at)
            return OnSaleState.Unannounced;

        if (now < at)
            return OnSaleState.Upcoming;

        return now < at.AddMinutes(OnSaleRecord.WindowEnd) ? OnSaleState.OnSaleNow : OnSaleState.Opened;
    }
}

/// <summary>What the Watchlist panel reads. An interface so a render test can hand the panel its rows.</summary>
public interface IWatchlistQueries
{
    /// <summary>Every enabled watch on an event that has not started, soonest first.</summary>
    Task<IReadOnlyList<WatchlistRow>> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// The Watchlist's read: the enabled watches, each source's latest word on each of their events,
/// and the per-market composition over those. Nothing here is precomputed, because "best price"
/// is only true until the next sweep and a cached board is one that lies quietly (ADR 0013).
///
/// <para>
/// A factory rather than a scoped context because a Blazor Server circuit lives for hours, and
/// a context that lived with it would hold every row the operator ever looked at.
/// </para>
/// </summary>
public sealed class WatchlistQueries(IDbContextFactory<TicketMiserDbContext> factory) : IWatchlistQueries
{
    public async Task<IReadOnlyList<WatchlistRow>> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var watches = await db.Watches
            .AsNoTracking()
            .Include(w => w.Event!).ThenInclude(e => e.Venue)
            .Include(w => w.Event!).ThenInclude(e => e.Performer)
            .Where(w => w.Enabled && w.Event!.StartsAt > now)
            .OrderBy(w => w.Event!.StartsAt)
            .ToListAsync(ct);

        if (watches.Count == 0)
            return [];

        var eventIds = watches.Select(w => w.EventId).Distinct().ToList();

        // Priced sources only. A feed names events and never prices them, and a disabled source's
        // last number is still its last number: the row says when it was observed.
        var sources = await db.Sources
            .AsNoTracking()
            .Where(s => s.Kind != SourceKind.Feed)
            .ToDictionaryAsync(s => s.Id, ct);

        // Newest observation per event and source. The stream is append-only and written only on
        // change, so "current" is the latest row rather than a column.
        var observations = await db.PriceObservations
            .AsNoTracking()
            .Where(o => eventIds.Contains(o.EventId))
            .GroupBy(o => new { o.EventId, o.SourceId })
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(ct);

        // Newest tick per event and source, for the sources the stream has nothing on — an
        // on-sale watch writes ticks before the sweep has written an observation — and for the
        // primary availability, which only the record carries.
        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => eventIds.Contains(t.EventId))
            .GroupBy(t => new { t.EventId, t.SourceId })
            .Select(g => g.OrderByDescending(t => t.ObservedAt).First())
            .ToListAsync(ct);

        // When primary last reported no tickets, per event. A later tick reporting tickets again
        // is the reappearance the board flags.
        var lastSoldOut = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => eventIds.Contains(t.EventId) && t.PrimaryStatus == InventoryStatus.NotAvailable)
            .GroupBy(t => t.EventId)
            .Select(g => new { EventId = g.Key, At = g.Max(t => t.ObservedAt) })
            .ToDictionaryAsync(x => x.EventId, x => x.At, ct);

        var observationsByEvent = observations.ToLookup(o => o.EventId);
        var ticksByEvent = ticks.ToLookup(t => t.EventId);

        return watches
            .Select(w => Compose(
                w,
                observationsByEvent[w.EventId],
                ticksByEvent[w.EventId],
                lastSoldOut.GetValueOrDefault(w.EventId),
                sources,
                now))
            .ToList();
    }

    /// <summary>
    /// One row from whatever each source last said. Pure over its inputs, so the composition can
    /// be pinned without a database; the per-market ranking itself lives in <see cref="MarketBest"/>.
    /// </summary>
    public static WatchlistRow Compose(
        Watch watch,
        IEnumerable<PriceObservation> latestObservations,
        IEnumerable<OnSaleTick> latestTicks,
        DateTimeOffset? lastSoldOutAt,
        IReadOnlyDictionary<int, Source> sources,
        DateTimeOffset now)
    {
        var evt = watch.Event ?? throw new InvalidOperationException($"Watch {watch.Id} has no event loaded.");

        var quotes = new Dictionary<int, SourceQuote>();

        foreach (var o in latestObservations)
        {
            if (sources.TryGetValue(o.SourceId, out var source))
                quotes[o.SourceId] = new SourceQuote(source, o.Lowest, o.AllIn, o.ListingCount, o.Currency, o.ObservedAt, null, false);
        }

        var tickList = latestTicks.ToList();

        foreach (var t in tickList)
        {
            if (!sources.TryGetValue(t.SourceId, out var source))
                continue;

            // The stream outranks the record for a price: it is written on every move, while a
            // tick is written only inside the on-sale window. A tick that is newer than the
            // stream's last row means the sale is running now and the tick is the truth.
            if (!quotes.TryGetValue(t.SourceId, out var existing) || t.ObservedAt > existing.ObservedAt)
                quotes[t.SourceId] = new SourceQuote(source, t.Lowest, t.AllIn, t.ListingCount, t.Currency, t.ObservedAt, t.PrimaryStatus, true);
        }

        var latestPrimaryTick = tickList
            .Where(t => sources.GetValueOrDefault(t.SourceId)?.Kind == SourceKind.Primary && t.PrimaryStatus is not null)
            .MaxBy(t => t.ObservedAt);

        var reappeared = latestPrimaryTick is not null
            && lastSoldOutAt is { } soldOut
            && latestPrimaryTick.ObservedAt > soldOut
            && latestPrimaryTick.PrimaryStatus is InventoryStatus.Available or InventoryStatus.FewLeft;

        return new WatchlistRow(
            watch,
            evt,
            WatchlistRow.StateOf(evt, now),
            MarketBest.Compose(SourceKind.Primary, quotes.Values),
            MarketBest.Compose(SourceKind.Resale, quotes.Values),
            latestPrimaryTick?.PrimaryStatus,
            latestPrimaryTick?.ObservedAt,
            reappeared);
    }
}
