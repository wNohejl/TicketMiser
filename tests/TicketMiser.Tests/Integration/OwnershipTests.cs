using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;
using TicketMiser.Web.Services;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// One owner never reads another's watches or purchases, through the very query services the
/// desk and the pages call; the operator — nobody signed in — reads every row, as before
/// accounts existed; and the scheduler polls the union, so two owners on one event are one
/// due event and one ref.
///
/// <para>
/// The database is shared by the collection, so the operator's assertions are made about this
/// test's own events; the accounts are this test's own, so theirs are exact.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class OwnershipTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<TicketMiserDbContext>
    {
        public TicketMiserDbContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed record World(Account A, Account B, Event Shared, Event OnlyB, Purchase APurchase, Purchase BPurchase, Purchase OperatorPurchase);

    /// <summary>
    /// A and B both watch <c>Shared</c>; B and the operator watch <c>OnlyB</c>. A bought for
    /// Shared, B for OnlyB, and the operator for Shared.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var venue = new Venue { Name = $"Ryman {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        db.Venues.Add(venue);
        var a = new Account { Email = $"a-{suffix}@example.com", CreatedAt = Now };
        var b = new Account { Email = $"b-{suffix}@example.com", CreatedAt = Now };
        db.Accounts.AddRange(a, b);
        await db.SaveChangesAsync();

        var shared = new Event
        {
            Name = $"Shared {suffix}",
            Slug = $"shared-{suffix}",
            VenueId = venue.Id,
            StartsAt = Now.AddDays(20),
            OnSaleAt = Now.AddMinutes(-5),
            ExternalIds = new Dictionary<string, string> { ["stub"] = $"ext-shared-{suffix}" }
        };
        var onlyB = new Event
        {
            Name = $"OnlyB {suffix}",
            Slug = $"onlyb-{suffix}",
            VenueId = venue.Id,
            StartsAt = Now.AddDays(25),
            ExternalIds = new Dictionary<string, string> { ["stub"] = $"ext-onlyb-{suffix}" }
        };
        db.Events.AddRange(shared, onlyB);
        await db.SaveChangesAsync();

        db.Watches.AddRange(
            new Watch { EventId = shared.Id, OwnerId = a.Id, CreatedAt = Now },
            new Watch { EventId = shared.Id, OwnerId = b.Id, CreatedAt = Now },
            new Watch { EventId = onlyB.Id, OwnerId = b.Id, CreatedAt = Now },
            new Watch { EventId = onlyB.Id, OwnerId = null, CreatedAt = Now });

        var aPurchase = new Purchase { EventId = shared.Id, OwnerId = a.Id, Quantity = 2, PaidPerTicket = 80m, PurchasedAt = Now.AddDays(-1) };
        var bPurchase = new Purchase { EventId = onlyB.Id, OwnerId = b.Id, Quantity = 1, PaidPerTicket = 95m, PurchasedAt = Now.AddDays(-2) };
        var opPurchase = new Purchase { EventId = shared.Id, OwnerId = null, Quantity = 4, PaidPerTicket = 70m, PurchasedAt = Now.AddDays(-3) };
        db.Purchases.AddRange(aPurchase, bPurchase, opPurchase);
        await db.SaveChangesAsync();

        return new World(a, b, shared, onlyB, aPurchase, bPurchase, opPurchase);
    }

    private WatchlistQueries Watchlist(OwnerScope owner) => new(new Factory(fixture), new FakeClock(Now), new FixedOwnerContext(owner));

    private PurchaseQueries Purchases(OwnerScope owner) => new(new Factory(fixture), new FakeClock(Now), new FixedOwnerContext(owner));

    private PriceHistoryQueries History(OwnerScope owner) => new(new Factory(fixture), new FakeClock(Now), new FixedOwnerContext(owner));

    [Fact]
    public async Task An_owner_reads_only_its_own_watches()
    {
        var w = await SeedAsync();

        var aRows = await Watchlist(OwnerScope.ForAccount(w.A.Id)).LoadAsync();
        var row = Assert.Single(aRows);
        Assert.Equal(w.Shared.Id, row.Event.Id);
        Assert.Equal(w.A.Id, row.Watch.OwnerId);

        var bRows = await Watchlist(OwnerScope.ForAccount(w.B.Id)).LoadAsync();
        Assert.Equal([w.Shared.Id, w.OnlyB.Id], bRows.Select(r => r.Event.Id));
        Assert.All(bRows, r => Assert.Equal(w.B.Id, r.Watch.OwnerId));

        // The picker's "already watched" flag is the reader's too.
        var aCandidates = await Watchlist(OwnerScope.ForAccount(w.A.Id)).CandidatesAsync(w.OnlyB.Name);
        Assert.False(Assert.Single(aCandidates).Watched);
    }

    [Fact]
    public async Task The_operator_reads_every_owner_s_watches_one_row_per_event_its_own_first()
    {
        var w = await SeedAsync();

        var rows = (await Watchlist(OwnerScope.Operator).LoadAsync())
            .Where(r => r.Event.Id == w.Shared.Id || r.Event.Id == w.OnlyB.Id)
            .ToList();

        Assert.Equal([w.Shared.Id, w.OnlyB.Id], rows.Select(r => r.Event.Id));
        // OnlyB has the operator's own watch, so the operator's row is drawn from it.
        Assert.Null(rows[1].Watch.OwnerId);
        // Shared has only fans' watches; the operator still sees it, because it is polled.
        Assert.NotNull(rows[0].Watch.OwnerId);
    }

    [Fact]
    public async Task An_owner_reads_only_its_own_purchases_and_the_operator_reads_all()
    {
        var w = await SeedAsync();

        var a = await Purchases(OwnerScope.ForAccount(w.A.Id)).LoadAsync();
        Assert.Equal([w.APurchase.Id], a.Select(l => l.Purchase.Id));

        // The receipt's read: A's purchases on the event, not the operator's on the same event.
        var aReceipt = await Purchases(OwnerScope.ForAccount(w.A.Id)).ForEventAsync(w.Shared.Id);
        Assert.Equal([w.APurchase.Id], aReceipt.Select(l => l.Purchase.Id));

        var b = await Purchases(OwnerScope.ForAccount(w.B.Id)).LoadAsync();
        Assert.Equal([w.BPurchase.Id], b.Select(l => l.Purchase.Id));

        var aSavings = await Purchases(OwnerScope.ForAccount(w.A.Id)).SavingsAsync();
        Assert.Equal(1, aSavings.Purchases);

        var operatorReceipt = await Purchases(OwnerScope.Operator).ForEventAsync(w.Shared.Id);
        Assert.Equal([w.OperatorPurchase.Id, w.APurchase.Id], operatorReceipt.Select(l => l.Purchase.Id));

        var all = (await Purchases(OwnerScope.Operator).LoadAsync()).Select(l => l.Purchase.Id).ToHashSet();
        Assert.Superset(new HashSet<long> { w.APurchase.Id, w.BPurchase.Id, w.OperatorPurchase.Id }, all);

        // Price history marks the reader's own purchases only.
        var history = await History(OwnerScope.ForAccount(w.A.Id)).LoadAsync(w.Shared.Id);
        Assert.Equal([w.APurchase.Id], history!.Purchases.Select(m => m.Purchase.Id));

        // And lists only events the reader watches.
        var choices = await History(OwnerScope.ForAccount(w.A.Id)).ChoicesAsync();
        Assert.Equal([w.Shared.Id], choices.Select(e => e.Id));
    }

    [Fact]
    public async Task An_owner_cannot_change_another_owner_s_rows()
    {
        var w = await SeedAsync();

        // A deleting B's purchase is a no-op; A stopping Shared stops A's watch only.
        await Purchases(OwnerScope.ForAccount(w.A.Id)).DeleteAsync(w.BPurchase.Id);
        await Watchlist(OwnerScope.ForAccount(w.A.Id)).UnwatchAsync(w.Shared.Id);

        await using var db = fixture.CreateContext();
        Assert.True(await db.Purchases.AnyAsync(p => p.Id == w.BPurchase.Id));
        Assert.False(await db.Watches.Where(x => x.EventId == w.Shared.Id && x.OwnerId == w.A.Id).Select(x => x.Enabled).SingleAsync());
        Assert.True(await db.Watches.Where(x => x.EventId == w.Shared.Id && x.OwnerId == w.B.Id).Select(x => x.Enabled).SingleAsync());

        // A watching OnlyB makes A's own row and leaves the operator's and B's alone.
        await Watchlist(OwnerScope.ForAccount(w.A.Id)).WatchAsync(w.OnlyB.Id);
        var onlyB = await db.Watches.AsNoTracking().Where(x => x.EventId == w.OnlyB.Id).ToListAsync();
        Assert.Equal(3, onlyB.Count);
        Assert.Contains(onlyB, x => x.OwnerId == w.A.Id && x.Enabled);

        // A purchase an account logs is its own.
        var logged = await Purchases(OwnerScope.ForAccount(w.B.Id)).LogAsync(new PurchaseDraft(w.Shared.Id, 1, 60m, true, null, Now.AddHours(-1), null));
        Assert.Equal(w.B.Id, (await db.Purchases.AsNoTracking().SingleAsync(p => p.Id == logged.Id)).OwnerId);
    }

    [Fact]
    public async Task The_account_page_reads_the_account_s_own_rows()
    {
        var w = await SeedAsync();
        var accounts = new AccountQueries(new Factory(fixture));

        var summary = await accounts.SummaryAsync(w.A.Id);
        Assert.NotNull(summary);
        Assert.Equal([w.Shared.Id], summary.Watching.Select(e => e.Id));
        Assert.Equal([w.APurchase.Id], summary.Purchases.Select(p => p.Id));

        Assert.True(await accounts.IsWatchingAsync(w.A.Id, w.Shared.Id));
        Assert.False(await accounts.IsWatchingAsync(w.A.Id, w.OnlyB.Id));
        Assert.Null(await accounts.SummaryAsync(int.MaxValue));
    }

    [Fact]
    public async Task Two_owners_watching_one_event_are_one_due_event_and_one_ref()
    {
        var w = await SeedAsync();
        await using var db = fixture.CreateContext();
        var clock = new FakeClock(Now);

        var onSale = new OnSaleWatchService(db, null!, null!, Options.Create(new IngestionOptions()), clock, NullLogger<OnSaleWatchService>.Instance);
        var due = await onSale.DueEventsAsync(Now, default);
        Assert.Single(due, e => e.Id == w.Shared.Id);

        var prices = new PriceIngestionService(db, null!, null!, clock, NullLogger<PriceIngestionService>.Instance);
        var refs = await prices.WatchlistRefsAsync(new StubPriceSource("stub", SourceKind.Resale), default);
        Assert.Single(refs, r => r.EventId == w.Shared.Id);
        Assert.Single(refs, r => r.EventId == w.OnlyB.Id);

        // The union's size: two more events, four more watches, three more owners at most
        // (A and B are new; the operator may already be counted by another test's rows).
        var size = await db.SizeAsync(Now);
        Assert.True(size.Watches - size.Events >= 2);
        Assert.True(size.Owners >= 3);
    }
}
