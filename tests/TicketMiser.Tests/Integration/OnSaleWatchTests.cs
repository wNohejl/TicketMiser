using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;
using TicketMiser.Reliability;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// The on-sale record, end to end on a fake clock: a watched event's window is walked in
/// one-minute steps, and every planned mark lands as a tick row for every source, with the
/// primary availability riding on the primary tick. Then the alert engine reads the record
/// and raises primary_reappeared.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Cadence")]
public class OnSaleWatchTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset T = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_mark_of_the_window_is_written_for_every_source_and_the_reappearance_is_seen()
    {
        var clock = new FakeClock(T - TimeSpan.FromMinutes(20));
        var options = new IngestionOptions();
        await using var db = fixture.CreateContext();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var primaryRow = new Source { Key = $"tm-{suffix}", Name = "Primary", Kind = SourceKind.Primary };
        var resaleRow = new Source { Key = $"sg-{suffix}", Name = "Resale", Kind = SourceKind.Resale };
        var inventoryRow = new Source { Key = "ticketmaster-inventory", Name = "Inventory", Kind = SourceKind.Primary };
        var marketplaceRow = new Source { Key = "ticketmaster-resale", Name = "Ticketmaster marketplace", Kind = SourceKind.Resale };
        db.Sources.AddRange(primaryRow, resaleRow);
        if (!await db.Sources.AnyAsync(s => s.Key == inventoryRow.Key))
            db.Sources.Add(inventoryRow);
        if (!await db.Sources.AnyAsync(s => s.Key == marketplaceRow.Key))
            db.Sources.Add(marketplaceRow);
        await db.SaveChangesAsync();

        var primary = new StubPriceSource(primaryRow.Key, SourceKind.Primary) { Clock = clock };
        var resale = new StubPriceSource(resaleRow.Key, SourceKind.Resale) { Clock = clock };
        var canonical = StubPriceSource.Bridgestone($"tm-ev-{suffix}", $"Sellout {suffix}", Start, T);
        primary.Events.Add(canonical);
        resale.Events.Add(canonical with { SourceEventId = $"sg-ev-{suffix}" });

        var resolver = new EventResolver(db);
        var evt = await resolver.ResolveEventAsync(primaryRow.Key, canonical, default);
        await resolver.ResolveEventAsync(resaleRow.Key, canonical with { SourceEventId = $"sg-ev-{suffix}" }, default);
        db.Watches.Add(new Watch { EventId = evt.Id, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();

        // The Inventory Status stub answers for the primary key: available, gone at T+20,
        // back at T+1h. The scripted shape of held-back inventory.
        var availability = new StubAvailabilitySource("ticketmaster-inventory")
        {
            Statuses = refs => refs.Select(r =>
            {
                var minutes = (clock.GetUtcNow() - T).TotalMinutes;
                var status = minutes < 20 ? InventoryStatus.Available
                    : minutes < 60 ? InventoryStatus.NotAvailable
                    : InventoryStatus.Available;
                return new CanonicalAvailability(r.SourceEventId, status, InventoryStatus.Available);
            }).ToList()
        };

        primary.Prices = (refs, now) => refs.Select(r =>
            new CanonicalPriceObservation(r.SourceEventId, now, "USD", 59.5m, null, 249.5m, null, true, EventStatusCode: "onsale")).ToList();
        resale.Prices = (refs, now) => refs.Select(r =>
            new CanonicalPriceObservation(r.SourceEventId, now, "USD", 120m + (decimal)(now - T).TotalMinutes, null, 900m, 300, false)).ToList();

        // A registry whose key mapping sends the inventory stub to the primary stub's ids: the
        // real inventory adapter maps to "ticketmaster", so the test registers the primary stub
        // under that name as well by giving the event a "ticketmaster" external id.
        evt.ExternalIds = new Dictionary<string, string>(evt.ExternalIds) { ["ticketmaster"] = canonical.SourceEventId };
        await db.SaveChangesAsync();

        var registry = new SourceRegistry([primary, resale], [availability]);
        var guard = new CreditBudgetGuard(new BudgetCalculator(db, clock), NullLogger<CreditBudgetGuard>.Instance);
        var watch = new OnSaleWatchService(db, registry, guard, Options.Create(options), clock, NullLogger<OnSaleWatchService>.Instance);

        // Walk the window a minute at a time, as the scheduler's tick would.
        var end = T + options.OnSaleWatch.Tail + options.OnSaleWatch.HourlyTail + TimeSpan.FromMinutes(5);
        while (clock.GetUtcNow() <= end)
        {
            await watch.TickAsync(default);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var expectedMarks = OnSaleWindow.Marks(T, options.OnSaleWatch).ToList();

        var primaryTicks = await db.OnSaleTicks
            .Where(t => t.EventId == evt.Id && t.SourceId == primaryRow.Id)
            .OrderBy(t => t.ObservedAt).ToListAsync();
        var resaleTicks = await db.OnSaleTicks
            .Where(t => t.EventId == evt.Id && t.SourceId == resaleRow.Id)
            .OrderBy(t => t.ObservedAt).ToListAsync();

        Assert.Equal(expectedMarks, primaryTicks.Select(t => t.ObservedAt));
        Assert.Equal(expectedMarks, resaleTicks.Select(t => t.ObservedAt));
        Assert.Equal(expectedMarks.Count, primary.PriceCalls);

        // The record shows what happened: available, then not, then back.
        Assert.Equal(InventoryStatus.Available, primaryTicks.First().PrimaryStatus);
        Assert.Contains(primaryTicks, t => t.PrimaryStatus == InventoryStatus.NotAvailable && t.MinutesFromOnSale == 20);
        Assert.Contains(primaryTicks, t => t.PrimaryStatus == InventoryStatus.Available && t.MinutesFromOnSale == 60);
        Assert.All(primaryTicks, t => Assert.True(t.AllIn));
        Assert.All(resaleTicks, t => Assert.False(t.AllIn));

        // Bridgestone has no marketplace listing of its own, yet Ticketmaster's marketplace
        // line exists for it at every mark: the resale status from the same availability
        // call, under the resale row, with no price and no all-in claim.
        var marketplaceId = (await db.Sources.SingleAsync(s => s.Key == "ticketmaster-resale")).Id;
        var marketplaceTicks = await db.OnSaleTicks
            .Where(t => t.EventId == evt.Id && t.SourceId == marketplaceId)
            .OrderBy(t => t.ObservedAt).ToListAsync();

        Assert.Equal(expectedMarks, marketplaceTicks.Select(t => t.ObservedAt));
        Assert.All(marketplaceTicks, t => Assert.Equal(InventoryStatus.Available, t.ResaleStatus));
        Assert.All(marketplaceTicks, t => Assert.Null(t.PrimaryStatus));
        Assert.All(marketplaceTicks, t => Assert.Null(t.Lowest));
        Assert.All(marketplaceTicks, t => Assert.Null(t.AllIn));

        // And the alert engine reads it as the one alert nobody else sends.
        var engine = new AlertEngine(db, new KpiCalculator(db), new BudgetCalculator(db),
            Options.Create(new ReliabilityOptions()), NullLogger<AlertEngine>.Instance);
        clock.Set(T + TimeSpan.FromHours(2));

        var candidates = await engine.EvaluateWatchesAsync(await db.Sources.ToListAsync(), default);

        var reappeared = Assert.Single(candidates, c => c.RuleKey == AlertRules.PrimaryReappeared && c.EventId == evt.Id);
        Assert.Equal(AlertSeverity.Warn, reappeared.Severity);
    }
}
