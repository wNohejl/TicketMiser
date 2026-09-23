using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The facts the first screen pins, read off ticks alone. Every minute is minutes-from-on-sale,
/// so the record reads against the announced time, and the on-sale price is a primary number or
/// nothing: a resale ask that arrived first is a different product and never stands in for it.
/// </summary>
public class OnSaleRecordTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };
    private static readonly Source Feed = new() { Id = 3, Key = "ticketmaster-feed", Name = "Discovery Feed", Kind = SourceKind.Feed };

    private static readonly Source[] Sources = [Ticketmaster, SeatGeek, Feed];

    private static OnSaleTick Primary(int minute, string status, decimal? lowest = null, bool? allIn = null) => new()
    {
        SourceId = Ticketmaster.Id,
        MinutesFromOnSale = minute,
        ObservedAt = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        PrimaryStatus = status,
        Lowest = lowest,
        AllIn = allIn
    };

    private static OnSaleTick Resale(int minute, int? listings, decimal? lowest = null) => new()
    {
        SourceId = SeatGeek.Id,
        MinutesFromOnSale = minute,
        ObservedAt = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        ListingCount = listings,
        Lowest = lowest,
        AllIn = false
    };

    [Fact]
    public void The_on_sale_price_is_the_first_priced_primary_tick_at_or_after_T_with_its_all_in_flag()
    {
        var ticks = new[]
        {
            Primary(-15, InventoryStatus.Unknown, lowest: 45m, allIn: false),   // a presale number, before T
            Resale(-5, 30, lowest: 210m),                                       // resale asks first, as they do
            Primary(0, InventoryStatus.Available),                              // opened without a price yet
            Primary(5, InventoryStatus.Available, lowest: 59.5m, allIn: true),
            Primary(10, InventoryStatus.Available, lowest: 64m, allIn: true)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.NotNull(facts.OnSalePrice);
        Assert.Equal(59.5m, facts.OnSalePrice.Lowest);
        Assert.True(facts.OnSalePrice.AllIn);
        Assert.Equal(Ticketmaster.Id, facts.OnSalePrice.SourceId);
        Assert.Equal(5, facts.OnSalePrice.Minute);
    }

    [Fact]
    public void A_resale_ask_is_never_the_on_sale_price()
    {
        var ticks = new[]
        {
            Resale(0, 40, lowest: 180m),
            Resale(5, 42, lowest: 175m),
            Primary(0, InventoryStatus.Available),
            Primary(5, InventoryStatus.NotAvailable)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Null(facts.OnSalePrice);
    }

    [Fact]
    public void The_sellout_is_the_first_not_available_minute_and_reappearance_the_first_tickets_after_it()
    {
        var ticks = new[]
        {
            Primary(0, InventoryStatus.Available, 59.5m, false),
            Primary(5, InventoryStatus.FewLeft, 59.5m, false),
            Primary(10, InventoryStatus.NotAvailable),
            Primary(15, InventoryStatus.NotAvailable),
            Primary(90, InventoryStatus.FewLeft, 59.5m, false),
            Primary(95, InventoryStatus.Available, 59.5m, false)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Equal(5, facts.FewLeftMinute);
        Assert.Equal(10, facts.SoldOutMinute);
        Assert.Equal(90, facts.ReappearedMinute);
    }

    [Fact]
    public void Order_of_input_does_not_matter_because_the_derivation_sorts_by_minute()
    {
        var ticks = new[]
        {
            Primary(95, InventoryStatus.Available),
            Primary(10, InventoryStatus.NotAvailable),
            Primary(0, InventoryStatus.Available, 50m, true)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Equal(10, facts.SoldOutMinute);
        Assert.Equal(95, facts.ReappearedMinute);
        Assert.Equal(0, facts.OnSalePrice?.Minute);
    }

    [Fact]
    public void No_sellout_means_no_reappearance_and_no_listings_at_sellout()
    {
        var ticks = new[]
        {
            Primary(0, InventoryStatus.Available, 50m, true),
            Primary(120, InventoryStatus.FewLeft, 50m, true),
            Resale(0, 12),
            Resale(120, 80)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Null(facts.SoldOutMinute);
        Assert.Null(facts.ReappearedMinute);
        Assert.Null(facts.ResaleListingsAtSellout);
    }

    [Fact]
    public void Listings_at_sellout_are_the_resale_count_as_it_stood_at_that_minute()
    {
        var ticks = new[]
        {
            Primary(0, InventoryStatus.Available, 50m, true),
            Primary(20, InventoryStatus.NotAvailable),
            Resale(0, 12),
            Resale(15, 210),
            Resale(25, 640)   // after the sellout: not what was on the shelf when it happened
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Equal(20, facts.SoldOutMinute);
        Assert.Equal(210, facts.ResaleListingsAtSellout);
    }

    [Fact]
    public void Listings_at_sellout_are_null_when_no_resale_source_had_reported_a_count_by_then()
    {
        var ticks = new[]
        {
            Primary(0, InventoryStatus.NotAvailable),
            Resale(0, null),
            Resale(5, 300)
        };

        var facts = OnSaleRecord.Derive(ticks, Sources);

        Assert.Equal(0, facts.SoldOutMinute);
        Assert.Null(facts.ResaleListingsAtSellout);
    }

    [Fact]
    public void Two_resale_sources_add_up_at_the_sellout_minute()
    {
        var stubHub = new Source { Id = 4, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale };
        var ticks = new[]
        {
            Primary(0, InventoryStatus.Available),
            Primary(10, InventoryStatus.NotAvailable),
            Resale(5, 100),
            new OnSaleTick { SourceId = stubHub.Id, MinutesFromOnSale = 10, ListingCount = 25 }
        };

        var facts = OnSaleRecord.Derive(ticks, [.. Sources, stubHub]);

        Assert.Equal(125, facts.ResaleListingsAtSellout);
    }

    [Fact]
    public void A_feed_source_contributes_nothing_and_an_empty_record_derives_to_nothing()
    {
        var feedOnly = new[]
        {
            new OnSaleTick { SourceId = Feed.Id, MinutesFromOnSale = 0, Lowest = 10m, PrimaryStatus = InventoryStatus.NotAvailable }
        };

        Assert.Equal(new OnSaleFacts(null, null, null, null, null), OnSaleRecord.Derive(feedOnly, Sources));
        Assert.Equal(new OnSaleFacts(null, null, null, null, null), OnSaleRecord.Derive([], Sources));
    }

    [Fact]
    public void Availability_maps_to_three_steps_and_unknown_to_nothing()
    {
        Assert.Equal(2, OnSaleRecord.AvailabilityLevel(InventoryStatus.Available));
        Assert.Equal(1, OnSaleRecord.AvailabilityLevel(InventoryStatus.FewLeft));
        Assert.Equal(0, OnSaleRecord.AvailabilityLevel(InventoryStatus.NotAvailable));
        Assert.Null(OnSaleRecord.AvailabilityLevel(InventoryStatus.Unknown));
        Assert.Null(OnSaleRecord.AvailabilityLevel(null));
    }
}
