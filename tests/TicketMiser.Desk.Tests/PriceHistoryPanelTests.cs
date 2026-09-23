using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;
using static TicketMiser.Desk.Tests.PriceFixtures;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Price history window. One chart per market and never one for both; every line named for
/// its source and fee basis; every price in the tables a link to its source with its basis
/// beside it; SeatGeek named; the purchase and the show marked; and opened about nothing, it
/// offers the watched events and starts on the soonest.
/// </summary>
public class PriceHistoryPanelTests : DeskTestContext
{
    private readonly FakePriceHistoryQueries _fake = new();
    private WindowManager _manager = default!;

    private IRenderedComponent<PriceHistoryPanel> Open(int? eventId = null)
    {
        _manager = new WindowManager(new AppWindowCatalog());
        Services.AddSingleton(_manager);
        Services.AddSingleton<IPriceHistoryQueries>(_fake);

        return eventId is { } id
            ? Render<PriceHistoryPanel>(p => p.AddUnmatched("EventId", id))
            : Render<PriceHistoryPanel>();
    }

    [Fact]
    public void Opened_about_nothing_with_nothing_watched_it_says_how_a_history_comes_to_exist()
    {
        var cut = Open();

        var empty = cut.Find(".empty--new").TextContent;

        Assert.Contains("Nothing is watched", empty);
        Assert.Contains("nothing is fetched without a watch", empty);
        Assert.Empty(cut.FindAll("figure.deskchart"));
        Assert.Empty(_fake.Loaded);
    }

    [Fact]
    public void Opened_about_nothing_it_starts_on_the_soonest_watched_event()
    {
        var soon = Event(7, "Soon Tour");
        var later = Event(8, "Later Tour");
        _fake.Choices.AddRange([soon, later]);
        _fake.Histories[7] = History(soon);

        var cut = Open();

        Assert.Equal([7], _fake.Loaded);
        Assert.Equal(2, cut.FindAll("option").Count);
        Assert.Equal(2, cut.FindAll("figure.deskchart").Count);
    }

    [Fact]
    public void An_event_with_no_prices_says_the_history_starts_with_the_first_sweep()
    {
        var evt = Event();
        _fake.Histories[7] = Core.Analytics.PriceHistory.Compose(evt, [], [], [], [], Sources);

        var cut = Open(7);

        Assert.Contains("No source has priced this event yet", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void The_two_markets_are_two_charts_and_each_line_names_its_source_and_basis()
    {
        var evt = Event();
        _fake.Histories[7] = History(evt);

        var cut = Open(7);

        var primary = cut.Find("section[data-market=primary]");
        var resale = cut.Find("section[data-market=resale]");

        var primaryChart = primary.QuerySelector("figure.deskchart")!;
        var resaleChart = resale.QuerySelector("figure.deskchart")!;

        Assert.Contains("Ticketmaster · face value", primaryChart.TextContent);
        Assert.DoesNotContain("SeatGeek", primaryChart.TextContent);

        Assert.Contains("SeatGeek · all-in", resaleChart.TextContent);
        Assert.Contains("StubHub · all-in", resaleChart.TextContent);
        Assert.DoesNotContain("Ticketmaster", resaleChart.TextContent);

        // One line, so the headline is that line's move; the resale headline ranks nobody.
        Assert.Contains("Ticketmaster face value up $5.50", primaryChart.QuerySelector(".deskchart__headline")!.TextContent);
        Assert.Equal("Resale: 2 sources, 2 lines", resaleChart.QuerySelector(".deskchart__headline")!.TextContent.Trim());
    }

    [Fact]
    public void Every_price_links_to_its_source_with_its_basis_and_seatgeek_is_named()
    {
        var evt = Event();
        _fake.Histories[7] = History(evt);

        var cut = Open(7);

        var seatGeekRow = cut.Find("tr[data-series=seatgeek]");
        var links = seatGeekRow.QuerySelectorAll("button.desklink");

        // Latest, low and high.
        Assert.Equal(3, links.Length);
        Assert.All(links, l => Assert.Equal("Open this event at SeatGeek", l.GetAttribute("title")));
        Assert.Contains("$84.00", links[0].TextContent);
        Assert.All(seatGeekRow.QuerySelectorAll(".tag"), t => Assert.Equal("all-in", t.TextContent.Trim()));

        var tmRow = cut.Find("tr[data-series=ticketmaster]");
        Assert.All(tmRow.QuerySelectorAll(".tag"), t => Assert.Equal("face value", t.TextContent.Trim()));

        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void The_purchase_and_the_show_are_marked()
    {
        var evt = Event();
        _fake.Histories[7] = History(evt);

        var cut = Open(7);

        var purchase = cut.Find("tr[data-mark=purchase]");
        Assert.Contains("$95.00", purchase.TextContent);
        Assert.Contains("2 tickets", purchase.TextContent);
        Assert.Contains("at SeatGeek", purchase.TextContent);
        Assert.Equal("Open this event at SeatGeek", purchase.QuerySelector("button.desklink")!.GetAttribute("title"));

        Assert.Contains("Starts", cut.Find("tr[data-mark=start]").TextContent);

        // The purchase's day reads "Bought" on the resale axis, and not on the primary one.
        Assert.Contains("Bought", cut.Find("section[data-market=resale] .deskchart__plot").TextContent);
        Assert.DoesNotContain("Bought", cut.Find("section[data-market=primary] .deskchart__plot").TextContent);
    }

    [Fact]
    public void The_event_name_opens_the_event_window()
    {
        var evt = Event();
        _fake.Histories[7] = History(evt);

        var cut = Open(7);

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open event").Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event);
        Assert.Equal(7, window.Parameters["EventId"]);
    }
}
