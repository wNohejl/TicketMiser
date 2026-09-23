using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketMiser.Core.Entities;

namespace TicketMiser.Data;

/// <summary>
/// Applies migrations, keeps partitions ahead of the clock, and seeds reference rows.
/// Safe to run on every start: each step is idempotent.
/// </summary>
public class DatabaseInitializer(TicketMiserDbContext db, ILogger<DatabaseInitializer> logger)
{
    /// <summary>The two partitioned tables, by the name the partition function takes.</summary>
    public static readonly string[] PartitionedTables = ["PriceObservations", "OnSaleTicks"];

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await EnsurePartitionsAsync(ct);
        await SeedCategoriesAsync(ct);
        await SeedSourcesAsync(ct);
        await SeedNashvilleVenuesAsync(ct);
        await MergeDuplicateVenuesAsync(ct);
        await RefileLegacyTicketmasterIdsAsync(ct);
        await MergeTwinEventsAsync(ct);
        await BackfillEventSlugsAsync(ct);
    }

    /// <summary>
    /// Moves Ticketmaster's legacy host id out of the <c>ticketmaster</c> key on events another
    /// source created. SeatGeek names events by that id, and until 2026-09-22 its adapter filed it
    /// where the Discovery id belongs: the id was then sent to the Discovery API, which cannot
    /// fetch it, and it kept Ticketmaster's own listing from resolving onto the event, so the
    /// show existed twice. Runs before <see cref="MergeTwinEventsAsync"/> so those twins merge.
    /// Only events Ticketmaster did not create are touched: an id Ticketmaster gave for its own
    /// event is its own, whatever it looks like. Idempotent.
    /// </summary>
    public async Task<int> RefileLegacyTicketmasterIdsAsync(CancellationToken ct = default)
    {
        const string ticketmaster = "ticketmaster";

        var candidates = await db.Events
            .Where(e => e.CreatedBySource == null || e.CreatedBySource != ticketmaster)
            .ToListAsync(ct);

        var moved = 0;
        foreach (var evt in candidates)
        {
            if (!evt.ExternalIds.TryGetValue(ticketmaster, out var id)
                || !Core.Contracts.ExternalIdKeys.IsTicketmasterLegacyId(id))
                continue;

            var ids = new Dictionary<string, string>(evt.ExternalIds);
            ids.Remove(ticketmaster);
            ids.TryAdd(Core.Contracts.ExternalIdKeys.TicketmasterLegacy, id);
            evt.ExternalIds = ids;
            moved++;
        }

        if (moved > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refiled {Count} Ticketmaster legacy ids out of the ticketmaster key", moved);
        }

        return moved;
    }

    /// <summary>
    /// The resolver's drift window, <c>EventResolver.SameFixtureDrift</c>, restated here because
    /// the data layer cannot see ingestion. A test holds the two equal.
    /// </summary>
    public static readonly TimeSpan SameFixtureDrift = TimeSpan.FromHours(6);

    /// <summary>A pair of rooms that are one room: the row kept and the row folded into it.</summary>
    public readonly record struct VenuePair(int KeptId, int DuplicateId);

    /// <summary>What folding one duplicate room into its kept row did, for the log and the tests.</summary>
    public sealed record VenueMergeCounts(
        VenuePair Pair, int EventsMerged, int EventsMoved, int Watches, int Purchases, int Observations, int Ticks,
        int FinalPrices, int Subscriptions, int Alerts, int SlugsAdopted, int SlugsRetired);

    /// <summary>
    /// Folds rooms that two sources named differently back into one. Before the resolver
    /// set aside a source's locality suffix, SeatGeek's "The Truth - Nashville" became a second
    /// room beside Ticketmaster's "The Truth", and because the event slow path never crosses
    /// rooms, every show there existed twice.
    ///
    /// <para>
    /// Per pair, in one transaction: each event at the duplicate room that has a twin at the
    /// kept room (same performer or same folded name, a start inside one fixture's drift, and
    /// no source naming them differently) is folded into the twin: ids unioned, every row that
    /// points at it repointed, the twin's slug kept. An event with no twin simply changes rooms.
    /// Then the empty room is deleted. Idempotent: a second run finds no pair and does nothing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<VenueMergeCounts>> MergeDuplicateVenuesAsync(CancellationToken ct = default)
    {
        var venues = await db.Venues.AsNoTracking().OrderBy(v => v.Id).ToListAsync(ct);
        var pairs = FindDuplicateVenues(venues);
        var results = new List<VenueMergeCounts>();

        foreach (var pair in pairs)
        {
            // The context retries on transient failure, so a user transaction must run inside
            // the strategy, and each attempt starts from a clean tracker.
            var strategy = db.Database.CreateExecutionStrategy();
            var counts = await strategy.ExecuteAsync(
                pair, (_, p, token) => MergeVenuePairAsync(p, token), verifySucceeded: null, ct);

            if (counts is null)
                continue;

            results.Add(counts);
            var kept = venues.First(v => v.Id == pair.KeptId);
            var duplicate = venues.First(v => v.Id == pair.DuplicateId);

            logger.LogInformation(
                "Merged venue {DuplicateId} '{DuplicateName}' into {KeptId} '{KeptName}': {Merged} event(s) folded into a twin, "
                + "{Moved} moved; repointed {Watches} watch(es), {Purchases} purchase(s), {Observations} observation(s), "
                + "{Ticks} tick(s), {Finals} final price(s), {Subscriptions} subscription(s), {Alerts} alert(s); "
                + "{Adopted} slug(s) taken from the duplicate, {Retired} retired",
                duplicate.Id, duplicate.Name, kept.Id, kept.Name, counts.EventsMerged, counts.EventsMoved,
                counts.Watches, counts.Purchases, counts.Observations, counts.Ticks, counts.FinalPrices,
                counts.Subscriptions, counts.Alerts, counts.SlugsAdopted, counts.SlugsRetired);
        }

        return results;
    }

    /// <summary>
    /// Which rooms are the same room: same country, state and town, and a fold in common,
    /// either the name with the room's own locality set aside (<see cref="VenueName.Key"/>) or
    /// the plain fold. The seeded row is kept where there is one, else the oldest; a seeded row
    /// is never the one deleted, or the next start would seed it again.
    /// </summary>
    public static IReadOnlyList<VenuePair> FindDuplicateVenues(IReadOnlyList<Venue> venues)
    {
        var seeded = NashvilleVenues.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var pairs = new List<VenuePair>();

        var towns = venues.GroupBy(v => (
            Country: v.CountryCode.ToUpperInvariant(),
            State: v.State.ToUpperInvariant(),
            City: v.City.ToLowerInvariant()));

        foreach (var town in towns)
        {
            var rooms = town.OrderBy(v => v.Id).ToList();
            if (rooms.Count < 2)
                continue;

            // Union on any shared fold: "Brooklyn Bowl - Nashville" meets "Brooklyn Bowl
            // Nashville" on the plain fold, "The Truth - Nashville" meets "The Truth" on the key.
            var parent = Enumerable.Range(0, rooms.Count).ToArray();
            int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);

            var firstWith = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < rooms.Count; i++)
            {
                var folds = new[]
                {
                    VenueName.Key(rooms[i].Name, rooms[i].City, rooms[i].State),
                    VenueName.Normalise(rooms[i].Name)
                };

                foreach (var fold in folds.Where(f => f.Length > 0))
                {
                    if (firstWith.TryGetValue(fold, out var other))
                        parent[Root(i)] = Root(other);
                    else
                        firstWith[fold] = i;
                }
            }

            foreach (var members in Enumerable.Range(0, rooms.Count).GroupBy(Root).Where(g => g.Count() > 1))
            {
                var set = members.Select(i => rooms[i]).OrderBy(v => v.Id).ToList();
                var kept = set.FirstOrDefault(v => seeded.Contains(v.Name)) ?? set[0];

                pairs.AddRange(set
                    .Where(v => v.Id != kept.Id && !seeded.Contains(v.Name))
                    .Select(v => new VenuePair(kept.Id, v.Id)));
            }
        }

        return pairs;
    }

    /// <summary>
    /// The event in <paramref name="room"/> that <paramref name="duplicate"/> is: a start inside
    /// one fixture's drift, the same performer or the same folded name, and not two listings
    /// the same source told apart (<see cref="MayBeOneFixture"/>). The resolver's slow-path
    /// rule, applied after the fact.
    /// </summary>
    public static Event? FindTwin(Event duplicate, IEnumerable<Event> room)
    {
        var name = VenueName.Normalise(duplicate.Name);

        return room
            .Where(e => e.Id != duplicate.Id)
            .Where(e => (e.StartsAt - duplicate.StartsAt).Duration() < SameFixtureDrift)
            .Where(e => (duplicate.PerformerId is not null && e.PerformerId == duplicate.PerformerId)
                        || VenueName.Normalise(e.Name) == name)
            .Where(e => MayBeOneFixture(duplicate, e))
            .OrderBy(e => (e.StartsAt - duplicate.StartsAt).Duration())
            .ThenBy(e => e.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether two rows can be one fixture as far as their ids go: true unless each carries,
    /// under the other's creating source, an id that source did not give it.
    ///
    /// <para>
    /// One direction is enough. A SeatGeek row carries SeatGeek's claim about the Ticketmaster
    /// id (its <c>ticketmaster</c> field), which is Ticketmaster's legacy host id and never
    /// equals the Discovery id on the Ticketmaster row; that claim must not keep the two apart,
    /// and the Ticketmaster row has no SeatGeek id to disagree with. Two listings one source
    /// gave different ids, such as an early and a late show, disagree in both directions.
    /// </para>
    /// </summary>
    public static bool MayBeOneFixture(Event a, Event b) => !Disagrees(a, b) || !Disagrees(b, a);

    /// <summary>Whether <paramref name="other"/> holds a different id for the source that created <paramref name="evt"/>.</summary>
    private static bool Disagrees(Event evt, Event other)
    {
        // A row of unknown origin: any shared source with different ids keeps them apart.
        if (evt.CreatedBySource is not { } creator)
        {
            return evt.ExternalIds.Any(kv =>
                other.ExternalIds.TryGetValue(kv.Key, out var id) && id != kv.Value);
        }

        return evt.ExternalIds.TryGetValue(creator, out var own)
               && other.ExternalIds.TryGetValue(creator, out var theirs)
               && own != theirs;
    }

    /// <summary>
    /// The two rows' ids as one map: for each source, the id from the row that source created
    /// (its own id outranks another source's claim about it), else the twin's, else the
    /// duplicate's.
    /// </summary>
    public static Dictionary<string, string> UnionIds(Event duplicate, Event twin)
    {
        var ids = new Dictionary<string, string>(twin.ExternalIds);

        foreach (var (source, id) in duplicate.ExternalIds)
        {
            if (!ids.ContainsKey(source) || (source == duplicate.CreatedBySource && source != twin.CreatedBySource))
                ids[source] = id;
        }

        return ids;
    }

    /// <summary>
    /// Folds twin events inside one room: the same show resolved twice at the same venue. The
    /// venue merge leaves these when a duplicate room's event could not be matched at the time,
    /// and the resolver makes them when SeatGeek named a show first with its legacy Ticketmaster
    /// id and Ticketmaster's own id then failed its slow path. The older row is kept, with its
    /// slug. One transaction per room; a second run finds nothing and does nothing.
    /// </summary>
    public async Task<IReadOnlyList<VenueMergeCounts>> MergeTwinEventsAsync(CancellationToken ct = default)
    {
        var events = await db.Events.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct);
        var rooms = events.GroupBy(e => e.VenueId)
            .Where(room => FindTwinsInRoom(room.ToList()).Count > 0)
            .Select(room => room.Key)
            .ToList();

        var results = new List<VenueMergeCounts>();

        foreach (var venueId in rooms)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var counts = await strategy.ExecuteAsync(
                venueId, (_, id, token) => MergeTwinsInRoomAsync(id, token), verifySucceeded: null, ct);

            if (counts is null || counts.EventsMerged == 0)
                continue;

            results.Add(counts);
            logger.LogInformation(
                "Folded {Merged} twin event(s) at venue {VenueId}; repointed {Watches} watch(es), {Purchases} purchase(s), "
                + "{Observations} observation(s), {Ticks} tick(s), {Finals} final price(s), {Subscriptions} subscription(s), "
                + "{Alerts} alert(s); {Adopted} slug(s) taken from the duplicate, {Retired} retired",
                counts.EventsMerged, venueId, counts.Watches, counts.Purchases, counts.Observations, counts.Ticks,
                counts.FinalPrices, counts.Subscriptions, counts.Alerts, counts.SlugsAdopted, counts.SlugsRetired);
        }

        return results;
    }

    /// <summary>
    /// Each later row in the room paired with the earlier row it is, oldest first, so a row is
    /// only ever folded into one that stays. Read-only: the fold itself pairs again as it goes,
    /// because a twin that has taken one row's ids may no longer match the next.
    /// </summary>
    public static IReadOnlyList<(Event Duplicate, Event Twin)> FindTwinsInRoom(IReadOnlyList<Event> room)
    {
        var kept = new List<Event>();
        var pairs = new List<(Event, Event)>();

        foreach (var evt in room.OrderBy(e => e.Id))
        {
            if (FindTwin(evt, kept) is { } twin)
                pairs.Add((evt, twin));
            else
                kept.Add(evt);
        }

        return pairs;
    }

    private async Task<VenueMergeCounts?> MergeTwinsInRoomAsync(int venueId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var room = await db.Events.Where(e => e.VenueId == venueId).OrderBy(e => e.Id).ToListAsync(ct);
        var kept = new List<Event>();
        var tally = new MergeTally();

        foreach (var evt in room)
        {
            if (FindTwin(evt, kept) is not { } twin)
            {
                kept.Add(evt);
                continue;
            }

            await MergeEventAsync(evt, twin, tally, ct);
            tally.EventsMerged++;
        }

        await tx.CommitAsync(ct);
        return tally.ToCounts(new VenuePair(venueId, venueId));
    }

    private async Task<VenueMergeCounts?> MergeVenuePairAsync(VenuePair pair, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var kept = await db.Venues.SingleOrDefaultAsync(v => v.Id == pair.KeptId, ct);
        var duplicate = await db.Venues.SingleOrDefaultAsync(v => v.Id == pair.DuplicateId, ct);
        if (kept is null || duplicate is null)
            return null;

        var keptEvents = await db.Events.Where(e => e.VenueId == kept.Id).ToListAsync(ct);
        var duplicateEvents = await db.Events
            .Where(e => e.VenueId == duplicate.Id)
            .OrderBy(e => e.StartsAt).ThenBy(e => e.Id)
            .ToListAsync(ct);

        var tally = new MergeTally();

        foreach (var evt in duplicateEvents)
        {
            var twin = FindTwin(evt, keptEvents);
            if (twin is null)
            {
                evt.VenueId = kept.Id;
                tally.EventsMoved++;
                continue;
            }

            await MergeEventAsync(evt, twin, tally, ct);
            tally.EventsMerged++;
        }

        await db.SaveChangesAsync(ct);

        // The room: the kept row's ids win where both have one; its gaps are filled.
        var ids = new Dictionary<string, string>(kept.ExternalIds);
        foreach (var (source, id) in duplicate.ExternalIds)
            ids.TryAdd(source, id);
        kept.ExternalIds = ids;
        kept.Timezone ??= duplicate.Timezone;
        kept.TicketingProvider ??= duplicate.TicketingProvider;
        kept.Capacity ??= duplicate.Capacity;

        db.Venues.Remove(duplicate);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return tally.ToCounts(pair);
    }

    private async Task MergeEventAsync(Event duplicate, Event twin, MergeTally tally, CancellationToken ct)
    {
        // Ids: each source's own id wins over another's claim about it, so every source's next
        // run takes the fast path onto the twin.
        twin.ExternalIds = UnionIds(duplicate, twin);

        twin.PerformerId ??= duplicate.PerformerId;
        twin.CategoryId ??= duplicate.CategoryId;
        twin.OnSaleAt ??= duplicate.OnSaleAt;
        if (twin.Presales == "[]" && duplicate.Presales != "[]")
            twin.Presales = duplicate.Presales;

        // Watches: one per owner per event (unique index). Where the owner already watches the
        // twin, that watch stands and carries over what the duplicate's asked for, so a watch
        // that was on stays on; otherwise the duplicate's moves across.
        var twinWatches = await db.Watches.Where(w => w.EventId == twin.Id).ToListAsync(ct);
        foreach (var duplicateWatch in await db.Watches.Where(w => w.EventId == duplicate.Id).ToListAsync(ct))
        {
            var twinWatch = twinWatches.FirstOrDefault(w => w.OwnerId == duplicateWatch.OwnerId);
            if (twinWatch is null)
            {
                duplicateWatch.EventId = twin.Id;
            }
            else
            {
                twinWatch.Enabled |= duplicateWatch.Enabled;
                twinWatch.NotifyOnDrop |= duplicateWatch.NotifyOnDrop;
                twinWatch.TargetPrice ??= duplicateWatch.TargetPrice;
                db.Watches.Remove(duplicateWatch);
            }

            tally.Watches++;
        }

        tally.Purchases += await db.Purchases.Where(p => p.EventId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.EventId, twin.Id), ct);

        // Both partitioned on ObservedAt, not EventId, so these updates stay inside their
        // partitions. They run before the delete because both foreign keys cascade, and the
        // ticks are the record.
        tally.Observations += await db.PriceObservations.Where(o => o.EventId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.EventId, twin.Id), ct);
        tally.Ticks += await db.OnSaleTicks.Where(t => t.EventId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.EventId, twin.Id), ct);

        // One final per event per source: where the twin already has that source's, it stands.
        var twinFinalSources = await db.FinalPrices.Where(f => f.EventId == twin.Id).Select(f => f.SourceId).ToListAsync(ct);
        await db.FinalPrices.Where(f => f.EventId == duplicate.Id && twinFinalSources.Contains(f.SourceId))
            .ExecuteDeleteAsync(ct);
        tally.FinalPrices += await db.FinalPrices.Where(f => f.EventId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.EventId, twin.Id), ct);

        tally.Alerts += await db.Alerts.Where(a => a.EventId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EventId, twin.Id), ct);

        // Subscriptions: one per address per event. Where the twin has the address already,
        // its row stands and takes the duplicate's deliveries; an unsubscribe on either holds.
        var twinSubscriptions = await db.Subscriptions.Where(x => x.EventId == twin.Id).ToListAsync(ct);
        var duplicateSubscriptions = await db.Subscriptions.Where(x => x.EventId == duplicate.Id).ToListAsync(ct);

        foreach (var subscription in duplicateSubscriptions)
        {
            var existing = twinSubscriptions.FirstOrDefault(x => x.Email == subscription.Email);
            if (existing is null)
            {
                subscription.EventId = twin.Id;
            }
            else
            {
                var delivered = await db.AlertDeliveries.Where(d => d.SubscriptionId == existing.Id)
                    .Select(d => d.AlertId).ToListAsync(ct);
                await db.AlertDeliveries
                    .Where(d => d.SubscriptionId == subscription.Id && !delivered.Contains(d.AlertId))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.SubscriptionId, existing.Id), ct);

                existing.ConfirmedAt ??= subscription.ConfirmedAt;
                existing.UnsubscribedAt ??= subscription.UnsubscribedAt;
                db.Subscriptions.Remove(subscription);
            }

            tally.Subscriptions++;
        }

        await db.SaveChangesAsync(ct);

        // The twin's address stands, so a link already shared keeps resolving. The exception:
        // only the duplicate's address was ever mailed to a subscriber, so that is the one out
        // in the world and the twin takes it.
        var duplicateSlug = duplicate.Slug;
        var adoptSlug = duplicateSlug is not null && duplicateSlug != twin.Slug
                        && duplicateSubscriptions.Count > 0 && twinSubscriptions.Count == 0;

        db.Events.Remove(duplicate);
        await db.SaveChangesAsync(ct);

        if (adoptSlug)
        {
            logger.LogWarning("Event {TwinId} takes slug {Slug} from merged event {DuplicateId}, which subscribers were sent; {Retired} retired",
                twin.Id, duplicateSlug, duplicate.Id, twin.Slug);
            twin.Slug = duplicateSlug;
            await db.SaveChangesAsync(ct);
            tally.SlugsAdopted++;
            tally.SlugsRetired++;
        }
        else if (duplicateSlug is not null && duplicateSlug != twin.Slug)
        {
            logger.LogInformation("Slug {Slug} of merged event {DuplicateId} retired in favour of {TwinSlug}",
                duplicateSlug, duplicate.Id, twin.Slug);
            tally.SlugsRetired++;
        }
    }

    private sealed class MergeTally
    {
        public int EventsMerged;
        public int EventsMoved;
        public int Watches;
        public int Purchases;
        public int Observations;
        public int Ticks;
        public int FinalPrices;
        public int Subscriptions;
        public int Alerts;
        public int SlugsAdopted;
        public int SlugsRetired;

        public VenueMergeCounts ToCounts(VenuePair pair) => new(
            pair, EventsMerged, EventsMoved, Watches, Purchases, Observations, Ticks, FinalPrices, Subscriptions, Alerts,
            SlugsAdopted, SlugsRetired);
    }

    /// <summary>
    /// Gives every event that predates the public record page its address. Idempotent: a row
    /// with a slug is never touched, so the link a fan already shared keeps resolving.
    /// </summary>
    public async Task BackfillEventSlugsAsync(CancellationToken ct = default)
    {
        var unaddressed = await db.Events
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Where(e => e.Slug == null)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        if (unaddressed.Count == 0)
            return;

        var taken = (await db.Events
                .Where(e => e.Slug != null)
                .Select(e => e.Slug!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var evt in unaddressed)
        {
            evt.Slug = EventSlug.Unique(EventSlug.Base(evt), taken);
            taken.Add(evt.Slug);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Addressed {Count} events that had no slug", unaddressed.Count);
    }

    /// <summary>
    /// Guarantees a partition exists for this month and the next two, on both partitioned
    /// tables. Called at startup and before each ingestion run, so crossing a month boundary
    /// is never an insert failure.
    /// </summary>
    public async Task EnsurePartitionsAsync(CancellationToken ct = default)
    {
        foreach (var table in PartitionedTables)
        {
            for (var offset = 0; offset <= 2; offset++)
            {
                var target = DateTimeOffset.UtcNow.AddMonths(offset);
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT ticketmiser_ensure_partition({0}, {1})", [table, target], ct);
            }
        }
    }

    private async Task SeedCategoriesAsync(CancellationToken ct)
    {
        var wanted = new (string Key, string Name)[]
        {
            ("concert", "Concerts"),
            ("sports", "Sports"),
            ("theater", "Theatre"),
            ("comedy", "Comedy")
        };

        var existing = await db.Categories.Select(c => c.Key).ToListAsync(ct);

        foreach (var (key, name) in wanted.Where(w => !existing.Contains(w.Key)))
            db.Categories.Add(new Category { Key = key, Name = name });

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded categories");
        }
    }

    /// <summary>
    /// Registers the providers with their published ceilings. These numbers are the contract
    /// the budget guard enforces. SeatGeek publishes none, so it is given a conservative ceiling
    /// of our own rather than treated as unlimited.
    /// </summary>
    private async Task SeedSourcesAsync(CancellationToken ct)
    {
        var wanted = new[]
        {
            new Source
            {
                Key = "ticketmaster",
                Name = "Ticketmaster",
                Kind = SourceKind.Primary,
                BaseUrl = "https://app.ticketmaster.com/discovery/v2/",
                RateLimitPerDay = 5000,
                RateLimitPerHour = 5 * 3600,
                Enabled = true
            },
            new Source
            {
                // Ticketmaster's own resale marketplace. For rooms it does not ticket the
                // Discovery API returns it as a second event with prices; for every room it
                // does, the Inventory Status API's resaleStatus is written here as a status
                // with no price. Same adapter, same key, same quota; a different market, so a
                // different source row.
                Key = "ticketmaster-resale",
                Name = "Ticketmaster marketplace",
                Kind = SourceKind.Resale,
                BaseUrl = "https://app.ticketmaster.com/discovery/v2/",
                Enabled = true
            },
            new Source
            {
                Key = "ticketmaster-inventory",
                Name = "Ticketmaster Inventory Status",
                Kind = SourceKind.Primary,
                BaseUrl = "https://app.ticketmaster.com/inventory-status/v1/",
                Enabled = true
            },
            new Source
            {
                Key = "ticketmaster-feed",
                Name = "Ticketmaster Discovery Feed",
                Kind = SourceKind.Feed,
                BaseUrl = "https://app.ticketmaster.com/discovery-feed/v2/",
                RateLimitPerDay = 4,
                Enabled = true
            },
            new Source
            {
                Key = "seatgeek",
                Name = "SeatGeek",
                Kind = SourceKind.Resale,
                BaseUrl = "https://api.seatgeek.com/2/",
                RateLimitPerHour = 600,
                Enabled = true
            }
        };

        var existing = await db.Sources.Select(s => s.Key).ToListAsync(ct);

        foreach (var source in wanted.Where(s => !existing.Contains(s.Key)))
            db.Sources.Add(source);

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded sources");
        }
    }

    /// <summary>
    /// The rooms and who tickets them, researched 2026-09-11. A venue arriving from a source is
    /// resolved onto one of these by name so the provider is known before the first price.
    /// </summary>
    public static readonly (string Name, string Provider, int? Capacity)[] NashvilleVenues =
    [
        ("Bridgestone Arena", "ticketmaster", 20000),
        ("Nissan Stadium", "ticketmaster", 69143),
        ("Ascend Amphitheater", "ticketmaster", 6800),
        ("FirstBank Amphitheater", "ticketmaster", 7500),
        ("Brooklyn Bowl Nashville", "ticketmaster", 1200),
        ("Marathon Music Works", "ticketmaster", 1500),
        ("Ryman Auditorium", "axs", 2362),
        ("Grand Ole Opry House", "axs", 4400),
        ("The Basement East", "axs", 400),
        ("The Basement", "axs", 150),
        ("Eastside Bowl", "axs", 800),
        ("Exit/In", "etix", 500),
        ("3rd & Lindsley", "etix", 600),
        ("The Caverns", "etix", 1200)
    ];

    private async Task SeedNashvilleVenuesAsync(CancellationToken ct)
    {
        var existing = await db.Venues
            .Where(v => v.City == "Nashville" || v.City == "Madison" || v.City == "Pelham" || v.City == "Franklin")
            .Select(v => v.Name)
            .ToListAsync(ct);

        foreach (var (name, provider, capacity) in NashvilleVenues.Where(v => !existing.Contains(v.Name)))
        {
            var city = name switch
            {
                "Eastside Bowl" => "Madison",
                "The Caverns" => "Pelham",
                "FirstBank Amphitheater" => "Franklin",
                _ => "Nashville"
            };

            db.Venues.Add(new Venue
            {
                Name = name,
                City = city,
                State = "TN",
                CountryCode = "US",
                Timezone = "America/Chicago",
                TicketingProvider = provider,
                Capacity = capacity
            });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded Nashville venues");
        }
    }
}
