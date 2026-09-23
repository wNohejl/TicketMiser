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
/// Two of the Watchlist's follow-ups. All sources lists every source's latest number, primary
/// then resale, in name order and with no best marked — listed, never ranked. Trend shows each
/// market's daily lowest per fee basis. Both link every price to its source and both lead on
/// to a fuller window.
/// </summary>
public class FollowUpPanelTests : DeskTestContext
{
    private readonly FakePriceHistoryQueries _fake = new();
    private WindowManager _manager = default!;

    private IRenderedComponent<T> Open<T>(int eventId = 7) where T : Microsoft.AspNetCore.Components.IComponent
    {
        _manager = new WindowManager(new AppWindowCatalog());
        Services.AddSingleton(_manager);
        Services.AddSingleton<IPriceHistoryQueries>(_fake);

        return Render<T>(p => p.AddUnmatched("EventId", eventId));
    }

    // ---- All sources ----------------------------------------------------------------------

    [Fact]
    public void All_sources_with_nothing_reported_says_a_watch_is_needed()
    {
        _fake.Quotes[7] = new EventQuotes(Event(), []);

        var cut = Open<AllSourcesPanel>();

        Assert.Contains("nothing is fetched without a watch", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void All_sources_for_an_unknown_event_is_an_error()
    {
        var cut = Open<AllSourcesPanel>(99);

        Assert.Contains("No event has id 99", cut.Find(".empty--error").TextContent);
    }

    [Fact]
    public void All_sources_lists_primary_then_resale_by_name_and_ranks_nothing()
    {
        _fake.Quotes[7] = new EventQuotes(Event(),
        [
            Quote(Ticketmaster, 59.5m, allIn: false, status: InventoryStatus.FewLeft),
            // Cheaper second on purpose: the order is the name's, never the price's.
            Quote(SeatGeek, 84m, allIn: true, listings: 412),
            Quote(StubHub, 70m, allIn: true, listings: 40)
        ]);

        var cut = Open<AllSourcesPanel>();

        var sections = cut.FindAll("section[data-market]");
        Assert.Equal(["primary", "resale"], sections.Select(s => s.GetAttribute("data-market")));

        Assert.Equal(["ticketmaster"], sections[0].QuerySelectorAll("tr[data-source]").Select(r => r.GetAttribute("data-source")));
        Assert.Equal(["seatgeek", "stubhub"], sections[1].QuerySelectorAll("tr[data-source]").Select(r => r.GetAttribute("data-source")));

        Assert.Contains("Few left", sections[0].TextContent);
        Assert.Contains("face value", sections[0].TextContent);
        Assert.Contains("412", sections[1].TextContent);

        Assert.Empty(cut.FindAll(".rail__tick--best"));
        Assert.DoesNotContain("Best", cut.Markup);

        var links = cut.FindAll("button.desklink").Where(l => l.GetAttribute("title")!.StartsWith("Open this event at", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, links.Count);
        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void All_sources_leads_to_the_event_window()
    {
        _fake.Quotes[7] = new EventQuotes(Event(), [Quote(SeatGeek, 84m, allIn: true)]);

        var cut = Open<AllSourcesPanel>();

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open event").Click();

        Assert.Equal(7, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event).Parameters["EventId"]);
    }

    // ---- Trend ----------------------------------------------------------------------------

    [Fact]
    public void A_trend_with_no_readings_says_a_trend_needs_a_watch()
    {
        var evt = Event();
        _fake.Trends[7] = PriceTrend.Compose(PriceHistory.Compose(evt, [], [], [], [], Sources), Now);

        var cut = Open<TrendPanel>();

        Assert.Contains("readings need a watch", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void A_trend_draws_one_small_chart_per_market_and_reads_now_against_a_week_ago()
    {
        var evt = Event();
        _fake.Trends[7] = PriceTrend.Compose(History(evt), Now);

        var cut = Open<TrendPanel>();

        var resale = cut.Find("section[data-market=resale]");
        var primary = cut.Find("section[data-market=primary]");

        Assert.Single(resale.QuerySelectorAll("figure.deskchart"));
        Assert.Single(primary.QuerySelectorAll("figure.deskchart"));

        var row = resale.QuerySelector("tr[data-basis=all-in]")!;
        var cells = row.QuerySelectorAll("td");

        // Now: SeatGeek's 84. A week ago: SeatGeek's 98, then the lowest all-in ask.
        Assert.Contains("$84.00", cells[1].TextContent);
        Assert.Equal("Open this event at SeatGeek", cells[1].QuerySelector("button.desklink")!.GetAttribute("title"));
        Assert.Contains("$98.00", cells[2].TextContent);
        Assert.Contains("−$14.00", cells[3].TextContent);

        Assert.Contains("face value", primary.QuerySelector("tr[data-basis=face]")!.TextContent);
        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void Full_history_opens_the_price_history_window_about_the_event()
    {
        var evt = Event();
        _fake.Trends[7] = PriceTrend.Compose(History(evt), Now);

        var cut = Open<TrendPanel>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Full history")).Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.PriceHistory);
        Assert.Equal(7, window.Parameters["EventId"]);
    }
}
