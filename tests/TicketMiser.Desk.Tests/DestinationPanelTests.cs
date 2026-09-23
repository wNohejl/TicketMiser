using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;
using static TicketMiser.Desk.Tests.PriceFixtures;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Performer and Venue destinations. Each lists upcoming events with where their sale stands
/// and each market's best in its own column, linked to its source; each says what it is missing
/// in words; and following an event, a performer or a venue opens that destination. The Venue
/// window says plainly when its room has no public primary feed.
/// </summary>
public class DestinationPanelTests : DeskTestContext
{
    private readonly FakeDestinationQueries _fake = new();
    private WindowManager _manager = default!;

    private IRenderedComponent<T> Open<T>(string parameter, int id) where T : Microsoft.AspNetCore.Components.IComponent
    {
        _manager = new WindowManager(new AppWindowCatalog());
        Services.AddSingleton(_manager);
        Services.AddSingleton<IDestinationQueries>(_fake);

        return Render<T>(p => p.AddUnmatched(parameter, id));
    }

    private static EventSummary Upcoming(Event evt, params SourceQuote[] quotes)
        => EventSummary.Compose(evt, quotes, watched: true, Now);

    private static PerformerDetail PerformerWith(params EventSummary[] upcoming)
        => new(Performer(), upcoming, []);

    // ---- Performer ------------------------------------------------------------------------

    [Fact]
    public void An_unknown_performer_is_an_error_not_an_empty_page()
    {
        var cut = Open<PerformerPanel>("PerformerId", 99);

        Assert.Contains("No performer has id 99", cut.Find(".empty--error").TextContent);
    }

    [Fact]
    public void A_performer_with_nothing_ahead_says_where_events_come_from()
    {
        _fake.PerformerDetails[20] = PerformerWith();

        var cut = Open<PerformerPanel>("PerformerId", 20);

        Assert.Contains("No upcoming event for this performer", cut.Find(".empty--new").TextContent);
        Assert.Contains("No past event here has a day-of price", cut.Markup);
    }

    [Fact]
    public void A_performers_events_carry_each_market_apart_linked_to_its_source()
    {
        var evt = Event();
        _fake.PerformerDetails[20] = PerformerWith(Upcoming(evt,
            Quote(Ticketmaster, 59.5m, allIn: false),
            Quote(SeatGeek, 84m, allIn: true, listings: 400)));

        var cut = Open<PerformerPanel>("PerformerId", 20);

        var primary = cut.Find("table[data-table=upcoming] td[data-market=primary]");
        var resale = cut.Find("table[data-table=upcoming] td[data-market=resale]");

        Assert.Contains("$59.50", primary.TextContent);
        Assert.Contains("face value", primary.TextContent);
        Assert.DoesNotContain("$84.00", primary.TextContent);
        Assert.Equal("Open this event at Ticketmaster", primary.QuerySelector("button.desklink")!.GetAttribute("title"));

        Assert.Contains("$84.00", resale.TextContent);
        Assert.Contains("all-in", resale.TextContent);
        Assert.Equal("Open this event at SeatGeek", resale.QuerySelector("button.desklink")!.GetAttribute("title"));

        Assert.Contains("Watching", cut.Find("table[data-table=upcoming]").TextContent);
        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void Following_an_event_or_its_venue_from_a_performer_opens_that_destination()
    {
        _fake.PerformerDetails[20] = PerformerWith(Upcoming(Event()));

        var cut = Open<PerformerPanel>("PerformerId", 20);

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open event").Click();
        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open venue").Click();

        Assert.Equal(7, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event).Parameters["EventId"]);
        Assert.Equal(30, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Venue).Parameters["VenueId"]);
    }

    // ---- Venue ----------------------------------------------------------------------------

    [Fact]
    public void An_axs_room_says_plainly_it_has_no_public_primary_feed()
    {
        var venue = Bridgestone(provider: "axs");
        _fake.VenueDetails[30] = new VenueDetail(venue, VenueFeed.For("axs"), [], []);

        var cut = Open<VenuePanel>("VenueId", 30);

        var note = cut.Find("[data-feed=none]");
        Assert.Contains("AXS, which has no public primary feed", note.TextContent);
        Assert.Contains("no primary feed", cut.Find("table[data-table=venue]").TextContent);
        Assert.Contains("No upcoming event at this venue", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void Etix_and_an_unknown_provider_are_said_in_words_too()
    {
        Assert.False(VenueFeed.For("etix").HasPrimaryFeed);
        Assert.Contains("Etix, which has no public primary feed", VenueFeed.For("etix").Sentence);
        Assert.Contains("not on record", VenueFeed.For(null).Sentence);
        Assert.True(VenueFeed.For("Ticketmaster").HasPrimaryFeed);
    }

    [Fact]
    public void A_venues_calendar_shows_the_on_sale_state_and_each_markets_best()
    {
        var evt = Event();
        evt.OnSaleAt = Now.AddDays(2);
        _fake.VenueDetails[30] = new VenueDetail(Bridgestone(), VenueFeed.For("ticketmaster"),
            [Upcoming(evt, Quote(SeatGeek, 84m, allIn: true), Quote(StubHub, 70m, allIn: false))], []);

        var cut = Open<VenuePanel>("VenueId", 30);

        Assert.Contains("primary tracked", cut.Find("table[data-table=venue]").TextContent);

        var row = cut.Find("table[data-table=upcoming] tbody tr");

        // A mixed fee basis in one market is refused with the reason, never ranked.
        Assert.Contains("Not ranked: SeatGeek all-in against StubHub face value.", row.QuerySelector("td[data-market=resale]")!.TextContent);
        Assert.Contains("No primary source has reported.", row.QuerySelector("td[data-market=primary]")!.TextContent);
    }

    [Fact]
    public void Following_an_event_or_its_performer_from_a_venue_opens_that_destination()
    {
        _fake.VenueDetails[30] = new VenueDetail(Bridgestone(), VenueFeed.For("ticketmaster"), [Upcoming(Event())], []);

        var cut = Open<VenuePanel>("VenueId", 30);

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open performer").Click();
        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open event").Click();

        Assert.Equal(20, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Performer).Parameters["PerformerId"]);
        Assert.Equal(7, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event).Parameters["EventId"]);
    }
}
