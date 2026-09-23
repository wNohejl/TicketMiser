using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>A performer on the Performers list, with how many events of theirs are still ahead.</summary>
/// <param name="NextZone">The next event's venue zone, so its date reads in the room's own calendar.</param>
public sealed record PerformerSummary(Performer Performer, int UpcomingEvents, DateTimeOffset? NextEventAt, string? NextZone = null)
{
    public const int Limit = 100;
}

/// <summary>
/// An upcoming event as a destination lists it: where its sale stands, and each market's best
/// number now. The two markets are composed apart by <see cref="MarketBest"/> and never ranked
/// against each other.
/// </summary>
public sealed record EventSummary(Event Event, OnSaleState OnSaleState, MarketBest Primary, MarketBest Resale, bool Watched)
{
    /// <summary>One event from its sources' latest quotes. Pure, so a render test can build one without a database.</summary>
    public static EventSummary Compose(Event evt, IEnumerable<SourceQuote> quotes, bool watched, DateTimeOffset now)
    {
        var list = quotes.ToList();

        return new EventSummary(
            evt,
            WatchlistRow.StateOf(evt, now),
            MarketBest.Compose(SourceKind.Primary, list),
            MarketBest.Compose(SourceKind.Resale, list),
            watched);
    }
}

/// <summary>
/// A past event and what its tickets cost on the day, per market: the final prices retention
/// promoted when it started. How a performer's or a room's prices have run.
/// </summary>
public sealed record PastEvent(Event Event, MarketBest Primary, MarketBest Resale)
{
    public const int Limit = 12;

    /// <summary>One past event from its final prices, each read as a quote on the day.</summary>
    public static PastEvent Compose(Event evt, IEnumerable<FinalPrice> finals, IReadOnlyDictionary<int, Source> sources)
    {
        var quotes = finals
            .Where(f => f.EventId == evt.Id)
            .Select(f => sources.GetValueOrDefault(f.SourceId) is { Kind: not SourceKind.Feed } s
                ? new SourceQuote(s, f.Lowest, f.AllIn, f.ListingCount, f.Currency, f.ObservedAt, null, false)
                : null)
            .OfType<SourceQuote>()
            .ToList();

        return new PastEvent(evt, MarketBest.Compose(SourceKind.Primary, quotes), MarketBest.Compose(SourceKind.Resale, quotes));
    }
}

/// <summary>
/// Who tickets a room, and whether that means the desk can read its box office. The provider
/// decides which source can see the primary market: Ticketmaster's rooms have one, and an AXS
/// or Etix room has none we may read (no public API, and no scraping — legal guidelines rule 1),
/// so its face value is simply not on the desk. The window says so rather than let a resale
/// number stand in for a face price.
/// </summary>
public sealed record VenueFeed(string Provider, bool HasPrimaryFeed, string Sentence)
{
    public static VenueFeed For(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "ticketmaster" => new("Ticketmaster", true,
            "Ticketed by Ticketmaster. The box office is read through Ticketmaster's Discovery and Inventory Status APIs."),
        "axs" => new("AXS", false,
            "Ticketed by AXS, which has no public primary feed. This room's face value and availability are not tracked; only resale numbers appear."),
        "etix" => new("Etix", false,
            "Ticketed by Etix, which has no public primary feed. This room's face value and availability are not tracked; only resale numbers appear."),
        null or "" => new("Unknown", false,
            "The ticketing provider is not on record, so whether the box office can be read is unknown."),
        var other => new(other, false,
            $"Ticketed by {other}, which is not a source the desk reads. Face value is not tracked here.")
    };
}

public sealed record PerformerDetail(Performer Performer, IReadOnlyList<EventSummary> Upcoming, IReadOnlyList<PastEvent> Past);

public sealed record VenueDetail(Venue Venue, VenueFeed Feed, IReadOnlyList<EventSummary> Upcoming, IReadOnlyList<PastEvent> Past);

/// <summary>What the Performers window and the Performer and Venue destinations read.</summary>
public interface IDestinationQueries
{
    /// <summary>
    /// Performers, narrowed by name, those with events ahead first, at most
    /// <see cref="PerformerSummary.Limit"/>.
    /// </summary>
    Task<IReadOnlyList<PerformerSummary>> PerformersAsync(string? search, CancellationToken ct = default);

    /// <summary>The performer's upcoming events and recent past ones. Null when no performer has the id.</summary>
    Task<PerformerDetail?> PerformerAsync(int performerId, CancellationToken ct = default);

    /// <summary>The room's upcoming events, recent past ones, and whether its box office can be read. Null when no venue has the id.</summary>
    Task<VenueDetail?> VenueAsync(int venueId, CancellationToken ct = default);
}

/// <summary>
/// The destinations' reads. Upcoming events carry each market's best from the sources' latest
/// quotes, the same rule the Watchlist reads by; past events carry their final prices.
/// </summary>
public sealed class DestinationQueries(IDbContextFactory<TicketMiserDbContext> factory, TimeProvider clock) : IDestinationQueries
{
    public async Task<IReadOnlyList<PerformerSummary>> PerformersAsync(string? search, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();

        var query = db.Performers.AsNoTracking();

        if (search?.Trim() is { Length: > 0 } term)
        {
            var like = "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            query = query.Where(p => EF.Functions.ILike(p.Name, like));
        }

        var rows = await query
            .Select(p => new
            {
                Performer = p,
                Upcoming = db.Events.Count(e => e.PerformerId == p.Id && e.StartsAt > now && e.Status != EventStatus.Cancelled),
                Next = db.Events
                    .Where(e => e.PerformerId == p.Id && e.StartsAt > now && e.Status != EventStatus.Cancelled)
                    .Min(e => (DateTimeOffset?)e.StartsAt),
                NextZone = db.Events
                    .Where(e => e.PerformerId == p.Id && e.StartsAt > now && e.Status != EventStatus.Cancelled)
                    .OrderBy(e => e.StartsAt)
                    .Select(e => e.Venue!.Timezone)
                    .FirstOrDefault()
            })
            .OrderByDescending(x => x.Upcoming > 0)
            .ThenBy(x => x.Performer.Name)
            .Take(PerformerSummary.Limit)
            .ToListAsync(ct);

        return rows.Select(r => new PerformerSummary(r.Performer, r.Upcoming, r.Next, r.NextZone)).ToList();
    }

    public async Task<PerformerDetail?> PerformerAsync(int performerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var performer = await db.Performers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == performerId, ct);
        if (performer is null)
            return null;

        var (upcoming, past) = await EventsAsync(db, db.Events.Where(e => e.PerformerId == performerId), ct);
        return new PerformerDetail(performer, upcoming, past);
    }

    public async Task<VenueDetail?> VenueAsync(int venueId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var venue = await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == venueId, ct);
        if (venue is null)
            return null;

        var (upcoming, past) = await EventsAsync(db, db.Events.Where(e => e.VenueId == venueId), ct);
        return new VenueDetail(venue, VenueFeed.For(venue.TicketingProvider), upcoming, past);
    }

    private async Task<(IReadOnlyList<EventSummary> Upcoming, IReadOnlyList<PastEvent> Past)> EventsAsync(
        TicketMiserDbContext db, IQueryable<Event> scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var upcoming = await scope
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Where(e => e.StartsAt > now && e.Status != EventStatus.Cancelled)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(ct);

        var past = await scope
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Where(e => e.StartsAt <= now)
            .OrderByDescending(e => e.StartsAt)
            .Take(PastEvent.Limit)
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);

        var upcomingIds = upcoming.Select(e => e.Id).ToList();
        var pastIds = past.Select(e => e.Id).ToList();

        var observations = await db.PriceObservations
            .AsNoTracking()
            .Where(o => upcomingIds.Contains(o.EventId))
            .GroupBy(o => new { o.EventId, o.SourceId })
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(ct);

        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => upcomingIds.Contains(t.EventId))
            .GroupBy(t => new { t.EventId, t.SourceId })
            .Select(g => g.OrderByDescending(t => t.ObservedAt).First())
            .ToListAsync(ct);

        var watched = (await db.Watches
            .Where(w => w.Enabled && upcomingIds.Contains(w.EventId))
            .Select(w => w.EventId)
            .ToListAsync(ct)).ToHashSet();

        var finals = await db.FinalPrices
            .AsNoTracking()
            .Where(f => pastIds.Contains(f.EventId))
            .ToListAsync(ct);

        return (
            upcoming
                .Select(e => EventSummary.Compose(e, LatestQuotes.For(e.Id, observations, ticks, sources), watched.Contains(e.Id), now))
                .ToList(),
            past.Select(e => PastEvent.Compose(e, finals, sources)).ToList());
    }
}
