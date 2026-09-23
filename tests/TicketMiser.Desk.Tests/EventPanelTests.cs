using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Event window's first tab. The record service is faked so the panel is judged on what it
/// says about a record, not on how it fetched one: that an unrecorded sale names when the window
/// opens, and that a recorded one pins the on-sale price with its fee status and its source, the
/// sellout minute, the reappearance, and the resale count at the sellout — every price cell a
/// link to the source that published it.
/// </summary>
public class EventPanelTests : DeskTestContext
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };

    private static Event Bridgestone(DateTimeOffset? onSaleAt) => new()
    {
        Id = 7,
        Name = "Example Tour",
        StartsAt = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
        OnSaleAt = onSaleAt,
        Venue = new Venue { Name = "Bridgestone Arena", City = "Nashville", State = "TN", Timezone = "America/Chicago", TicketingProvider = "ticketmaster" },
        Performer = new Performer { Name = "Example Performer" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    private sealed class FakeRecords(OnSaleRecord? record) : IOnSaleRecordService
    {
        public Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default)
            => Task.FromResult(eventId == record?.Event.Id ? record : null);

        public Task<OnSaleRecord?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(slug == record?.Event.Slug ? record : null);

        public Task<string?> SlugForAsync(int eventId, CancellationToken ct = default)
            => Task.FromResult(eventId == record?.Event.Id ? record.Event.Slug : null);
    }

    private readonly FakePriceHistoryQueries _histories = new();
    private WindowManager _manager = default!;

    private IRenderedComponent<EventPanel> Open(OnSaleRecord? record, int eventId = 7)
    {
        _manager = new WindowManager(new TicketMiser.Web.Windowing.AppWindowCatalog());
        Services.AddSingleton(_manager);
        Services.AddSingleton<IOnSaleRecordService>(new FakeRecords(record));
        Services.AddSingleton<IPriceHistoryQueries>(_histories);

        return Render<EventPanel>(p => p.AddUnmatched("EventId", eventId));
    }

    private static OnSaleTick Tick(Source source, int minute, string? primaryStatus = null, decimal? lowest = null, bool? allIn = null, int? listings = null) => new()
    {
        EventId = 7,
        SourceId = source.Id,
        MinutesFromOnSale = minute,
        ObservedAt = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero).AddMinutes(minute),
        PrimaryStatus = primaryStatus,
        Lowest = lowest,
        Highest = lowest is { } l ? l * 2 : null,
        AllIn = allIn,
        ListingCount = listings
    };

    private static OnSaleRecord Recorded()
    {
        var ticks = new List<OnSaleTick>
        {
            Tick(Ticketmaster, -15, InventoryStatus.Unknown),
            Tick(Ticketmaster, 0, InventoryStatus.Available, 59.5m, allIn: true),
            Tick(Ticketmaster, 5, InventoryStatus.FewLeft, 59.5m, allIn: true),
            Tick(Ticketmaster, 10, InventoryStatus.NotAvailable),
            Tick(Ticketmaster, 95, InventoryStatus.Available, 59.5m, allIn: true),
            Tick(SeatGeek, -15, listings: 12, lowest: 210m, allIn: false),
            Tick(SeatGeek, 10, listings: 412, lowest: 240m, allIn: false),
            Tick(SeatGeek, 95, listings: 380, lowest: 199m, allIn: false)
        };

        var sources = new[] { Ticketmaster, SeatGeek };
        var evt = Bridgestone(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero));

        return new OnSaleRecord(evt, sources, ticks, OnSaleRecord.Derive(ticks, sources));
    }

    [Fact]
    public void With_no_ticks_the_first_tab_says_the_window_is_unrecorded_and_when_the_sale_opens()
    {
        var evt = Bridgestone(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero));
        var record = new OnSaleRecord(evt, [], [], OnSaleRecord.Derive([], []));

        var cut = Open(record);

        var empty = cut.Find(".empty--new");

        Assert.Contains("has not been recorded", empty.TextContent);
        // 15:00 UTC on 18 September is 10:00 in Nashville, which is the time the sale was announced in.
        Assert.Contains("Fri 18 Sep 2026 10:00 America/Chicago", empty.TextContent);
        Assert.Empty(cut.FindAll(".metric"));
    }

    [Fact]
    public void An_unannounced_on_sale_says_so_rather_than_naming_a_time()
    {
        var evt = Bridgestone(null);
        evt.OnSaleTbd = true;

        var cut = Open(new OnSaleRecord(evt, [], [], OnSaleRecord.Derive([], [])));

        Assert.Contains("No on-sale time has been announced", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void An_unknown_event_is_reported_as_an_error_not_as_an_empty_record()
    {
        var cut = Open(Recorded(), eventId: 999);

        Assert.Contains("No event has id 999", cut.Find(".empty--error").TextContent);
    }

    [Fact]
    public void The_metrics_pin_the_on_sale_price_the_sellout_the_reappearance_and_the_resale_count()
    {
        var cut = Open(Recorded());

        var metrics = cut.FindAll(".metric");
        Assert.Equal(4, metrics.Count);

        var price = metrics[0];
        Assert.Equal("$59.50", price.QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Equal("all-in at Ticketmaster, T", price.QuerySelector(".metric__note")!.TextContent);

        var soldOut = metrics[1];
        Assert.Equal("T+10 min", soldOut.QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Contains("metric--bad", soldOut.ClassList);

        var back = metrics[2];
        Assert.Equal("T+95 min", back.QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Contains("metric--warn", back.ClassList);

        var listings = metrics[3];
        Assert.Equal("412", listings.QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Equal("on SeatGeek at T+10 min", listings.QuerySelector(".metric__note")!.TextContent);
    }

    [Fact]
    public void The_two_markets_are_drawn_as_two_charts_and_never_on_one_axis()
    {
        var cut = Open(Recorded());

        var charts = cut.FindAll("figure.deskchart");

        Assert.Equal(2, charts.Count);
        Assert.Contains("Sold out T+10 min, face value back T+95 min", charts[0].QuerySelector(".deskchart__headline")!.TextContent);
        Assert.Contains("412 resale listings at the sellout minute", charts[1].QuerySelector(".deskchart__headline")!.TextContent);

        // Each chart legends its own market's sources and nobody else's.
        Assert.Contains("Ticketmaster", charts[0].TextContent);
        Assert.DoesNotContain("SeatGeek", charts[0].TextContent);
        Assert.Contains("SeatGeek", charts[1].TextContent);
        Assert.DoesNotContain("Ticketmaster", charts[1].TextContent);
    }

    [Fact]
    public void Every_price_cell_links_to_its_source_and_seatgeek_is_named_beside_its_numbers()
    {
        var cut = Open(Recorded());

        var links = cut.FindAll("button.desklink");

        // Ticketmaster: 3 priced ticks × lowest and highest. SeatGeek: 3 ticks × lowest, highest and listings.
        Assert.Equal(15, links.Count);
        Assert.All(links, l => Assert.Contains("Open this event at", l.GetAttribute("title")));
        Assert.Equal(9, links.Count(l => l.GetAttribute("title") == "Open this event at SeatGeek"));

        // The fee status stands beside every price, never implied.
        var fees = cut.FindAll(".tag").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(3, fees.Count(f => f == "all-in"));
        Assert.Equal(3, fees.Count(f => f == "face value"));

        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void The_details_tab_names_the_event_the_venue_the_provider_and_the_on_sale_time()
    {
        var cut = Open(Recorded());

        cut.FindAll("[role=tab]").Last().Click();

        var text = cut.Find("table.grid").TextContent;

        Assert.Contains("Example Tour", text);
        Assert.Contains("Bridgestone Arena, Nashville, TN", text);
        Assert.Contains("Example Performer", text);
        Assert.Contains("ticketmaster", text);
        Assert.Contains("Fri 18 Sep 2026 10:00 America/Chicago", text);
    }

    [Fact]
    public void The_price_history_tab_draws_the_history_one_chart_per_market()
    {
        var record = Recorded();
        _histories.Histories[7] = PriceFixtures.History(record.Event);

        var cut = Open(record);

        // The history is read only when the tab is opened.
        Assert.Empty(_histories.Loaded);

        cut.FindAll("[role=tab]")[1].Click();

        Assert.Equal([7], _histories.Loaded);
        Assert.Contains("Ticketmaster · face value", cut.Find("section[data-market=primary] figure.deskchart").TextContent);
        Assert.Contains("SeatGeek · all-in", cut.Find("section[data-market=resale] figure.deskchart").TextContent);
        Assert.NotNull(cut.Find("tr[data-mark=purchase]"));
    }

    [Fact]
    public void The_details_tab_follows_the_performer_and_the_venue_to_their_windows()
    {
        var record = Recorded();
        record.Event.PerformerId = 20;
        record.Event.Performer!.Id = 20;
        record.Event.VenueId = 30;
        record.Event.Venue!.Id = 30;

        var cut = Open(record);
        cut.FindAll("[role=tab]").Last().Click();

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open performer").Click();
        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open venue").Click();

        var performer = Assert.Single(_manager.Windows, w => w.Definition.Key == TicketMiser.Web.Windowing.WindowCatalog.Performer);
        Assert.Equal(20, performer.Parameters["PerformerId"]);
        Assert.Equal("Example Performer", performer.Title);

        var venue = Assert.Single(_manager.Windows, w => w.Definition.Key == TicketMiser.Web.Windowing.WindowCatalog.Venue);
        Assert.Equal(30, venue.Parameters["VenueId"]);
    }

    [Fact]
    public void A_source_link_goes_to_the_event_on_the_source_site_or_the_site_when_it_has_no_id()
    {
        var evt = Bridgestone(null);

        Assert.Equal("https://www.ticketmaster.com/event/tm-123", SourceLink.For(Ticketmaster, evt));
        Assert.Equal("https://seatgeek.com/e/events/6543210", SourceLink.For(SeatGeek, evt));

        var resale = new Source { Key = "ticketmaster-resale", Name = "Ticketmaster resale", Kind = SourceKind.Resale };
        Assert.Equal("https://www.ticketmaster.com/event/tm-123", SourceLink.For(resale, evt));

        evt.ExternalIds.Clear();
        Assert.Equal("https://seatgeek.com", SourceLink.For(SeatGeek, evt));

        var other = new Source { Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale, BaseUrl = "https://api.stubhub.example/" };
        Assert.Equal("https://api.stubhub.example/", SourceLink.For(other, evt));
    }
}
