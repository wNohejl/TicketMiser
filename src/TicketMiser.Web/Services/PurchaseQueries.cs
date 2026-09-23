using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// What the Log purchase form starts from: the event, the sources a ticket to it could have come
/// from, and each market's best number now, composed the way the Watchlist composes it.
/// </summary>
/// <param name="Sources">Priced sources that know this event (an external id, or a reading), primary first.</param>
/// <param name="Primary">Built from primary quotes only.</param>
/// <param name="Resale">Built from resale quotes only. The two are never compared.</param>
public sealed record PurchaseForm(Event Event, IReadOnlyList<Source> Sources, MarketBest Primary, MarketBest Resale);

/// <summary>A draft the ledger refused, with what was wrong per field.</summary>
public sealed class PurchaseValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception(string.Join(" ", errors.Values))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>
/// What the Purchases, Savings and Log purchase windows read and write. An interface so a render
/// test can hand each panel its rows.
///
/// <para>
/// Purchases are personal. Every read and write here is on behalf of the
/// <see cref="IOwnerContext"/>: a signed-in account reads, logs and deletes its own; the
/// operator (the desk, with nobody signed in) reads every purchase and logs unowned ones
/// (see <see cref="Ownership"/>).
/// </para>
/// </summary>
public interface IPurchaseQueries
{
    /// <summary>Every purchase, newest first, each with its event, venue, source and grade.</summary>
    Task<IReadOnlyList<PurchaseLine>> LoadAsync(CancellationToken ct = default);

    /// <summary>The purchases logged against one event, oldest first.</summary>
    Task<IReadOnlyList<PurchaseLine>> ForEventAsync(int eventId, CancellationToken ct = default);

    /// <summary>Paid against day-of prices, each fee basis totalled apart.</summary>
    Task<SavingsSummary> SavingsAsync(CancellationToken ct = default);

    /// <summary>The form's starting point, or null when no event has that id. A watch is not required.</summary>
    Task<PurchaseForm?> FormAsync(int eventId, CancellationToken ct = default);

    /// <summary>
    /// Logs a purchase. Throws <see cref="PurchaseValidationException"/> when the draft is not one,
    /// and <see cref="InvalidOperationException"/> when its event or source does not exist.
    /// </summary>
    Task<Purchase> LogAsync(PurchaseDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Removes a purchase the reader may see: an account its own, the operator any. It is the
    /// reader's own record; there is nothing to keep it for. False when no such row was the
    /// reader's to remove — another account's id deletes nothing.
    /// </summary>
    Task<bool> DeleteAsync(long purchaseId, CancellationToken ct = default);
}

/// <summary>
/// The ledger's queries. A context per call, like the rest: a Blazor circuit lives for hours and
/// a context that lived with it would hold every row the operator ever looked at.
/// </summary>
public sealed class PurchaseQueries(IDbContextFactory<TicketMiserDbContext> factory, TimeProvider clock, IOwnerContext owners) : IPurchaseQueries
{
    public Task<IReadOnlyList<PurchaseLine>> LoadAsync(CancellationToken ct = default)
        => ReadAsync(null, ct);

    public Task<IReadOnlyList<PurchaseLine>> ForEventAsync(int eventId, CancellationToken ct = default)
        => ReadAsync(eventId, ct);

    public async Task<SavingsSummary> SavingsAsync(CancellationToken ct = default)
        => PurchaseLedger.Summarise(await ReadAsync(null, ct));

    public async Task<PurchaseForm?> FormAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var evt = await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (evt is null)
            return null;

        var sources = await db.Sources
            .AsNoTracking()
            .Where(s => s.Kind != SourceKind.Feed)
            .ToDictionaryAsync(s => s.Id, ct);

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

        return Compose(evt, observations, ticks, sources, clock.GetUtcNow());
    }

    /// <summary>
    /// The form's starting point from each source's latest word. Reuses the Watchlist's
    /// composition, handed a watch that exists only for the call: a purchase needs no watch, and
    /// the best-per-market rule must be the one the board shows.
    /// </summary>
    public static PurchaseForm Compose(
        Event evt,
        IEnumerable<PriceObservation> latestObservations,
        IEnumerable<OnSaleTick> latestTicks,
        IReadOnlyDictionary<int, Source> sources,
        DateTimeOffset now)
    {
        var obs = latestObservations.ToList();
        var ticks = latestTicks.ToList();
        var row = WatchlistQueries.Compose(new Watch { EventId = evt.Id, Event = evt }, obs, ticks, null, sources, now);

        var reported = obs.Select(o => o.SourceId).Concat(ticks.Select(t => t.SourceId)).ToHashSet();
        var known = sources.Values
            .Where(s => s.Kind != SourceKind.Feed)
            .Where(s => reported.Contains(s.Id) || HasExternalId(evt, s))
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new PurchaseForm(evt, known, row.Primary, row.Resale);
    }

    /// <summary>Whether the event carries an id at this source, the way <see cref="SourceLink"/> looks it up.</summary>
    public static bool HasExternalId(Event evt, Source source)
    {
        var key = source.Key.StartsWith("ticketmaster", StringComparison.Ordinal) ? "ticketmaster" : source.Key;
        return evt.ExternalIds.TryGetValue(key, out var id) && id.Length > 0;
    }

    public async Task<Purchase> LogAsync(PurchaseDraft draft, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var errors = PurchaseLedger.Validate(draft, now);
        if (errors.Count > 0)
            throw new PurchaseValidationException(errors);

        await using var db = await factory.CreateDbContextAsync(ct);

        if (!await db.Events.AnyAsync(e => e.Id == draft.EventId, ct))
            throw new InvalidOperationException($"No event has id {draft.EventId}.");

        if (draft.SourceId is { } sourceId && !await db.Sources.AnyAsync(s => s.Id == sourceId && s.Kind != SourceKind.Feed, ct))
            throw new InvalidOperationException($"No priced source has id {sourceId}.");

        var purchase = PurchaseLedger.ToPurchase(draft, now);
        purchase.OwnerId = (await owners.GetAsync(ct)).OwnerId;
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync(ct);

        return purchase;
    }

    public async Task<bool> DeleteAsync(long purchaseId, CancellationToken ct = default)
    {
        var owner = await owners.GetAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Purchases.VisibleTo(owner).Where(p => p.Id == purchaseId).ExecuteDeleteAsync(ct) > 0;
    }

    private async Task<IReadOnlyList<PurchaseLine>> ReadAsync(int? eventId, CancellationToken ct)
    {
        var owner = await owners.GetAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);

        var query = db.Purchases
            .AsNoTracking()
            .VisibleTo(owner)
            .Include(p => p.Event!).ThenInclude(e => e.Venue)
            .Include(p => p.Event!).ThenInclude(e => e.Performer)
            .AsQueryable();

        if (eventId is { } id)
            query = query.Where(p => p.EventId == id);

        var purchases = await query
            .OrderByDescending(p => p.PurchasedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync(ct);

        if (purchases.Count == 0)
            return [];

        var eventIds = purchases.Select(p => p.EventId).Distinct().ToList();

        var finals = await db.FinalPrices
            .AsNoTracking()
            .Where(f => eventIds.Contains(f.EventId))
            .ToListAsync(ct);

        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var finalsByEvent = finals.ToLookup(f => f.EventId);
        var now = clock.GetUtcNow();

        var lines = purchases
            .Select(p => PurchaseLedger.Line(p, p.Event!, finalsByEvent[p.EventId], sources, now))
            .ToList();

        return eventId is null ? lines : lines.OrderBy(l => l.Purchase.PurchasedAt).ToList();
    }
}
