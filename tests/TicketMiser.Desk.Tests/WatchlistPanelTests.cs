using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The board. The query is faked so the panel is judged on what it says about a row, not on how
/// it fetched one: that an empty watchlist says how a watch comes to exist, that the two markets
/// sit in two cells and never share a number, that a mixed fee basis is refused with a reason,
/// that the rail carries one tick per source, and that opening a row reaches the Event window
/// about that event — the only route to it the desk has.
/// </summary>
public class WatchlistPanelTests : DeskTestContext
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };
    private static readonly Source StubHub = new() { Id = 3, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale, BaseUrl = "https://api.stubhub.example/" };

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class FakeWatchlist(IReadOnlyList<WatchlistRow> rows, IReadOnlyList<WatchCandidate>? candidates = null) : IWatchlistQueries
    {
        public List<int> Watched { get; } = [];
        public List<int> Unwatched { get; } = [];
        public List<string?> Searches { get; } = [];

        public Task<IReadOnlyList<WatchlistRow>> LoadAsync(CancellationToken ct = default) => Task.FromResult(rows);

        public Task<IReadOnlyList<WatchCandidate>> CandidatesAsync(string? search, CancellationToken ct = default)
        {
            Searches.Add(search);
            return Task.FromResult(candidates ?? []);
        }

        public Task WatchAsync(int eventId, CancellationToken ct = default)
        {
            Watched.Add(eventId);
            return Task.CompletedTask;
        }

        public Task UnwatchAsync(int eventId, CancellationToken ct = default)
        {
            Unwatched.Add(eventId);
            return Task.CompletedTask;
        }
    }

    private FakeWatchlist _fake = default!;

    private WindowManager _manager = default!;

    private readonly FakeTimeProvider _clock = new(Now);

    private IRenderedComponent<WatchlistPanel> Render(params WatchlistRow[] rows) => RenderWith(rows, null);

    private IRenderedComponent<WatchlistPanel> RenderWith(IReadOnlyList<WatchlistRow> rows, IReadOnlyList<WatchCandidate>? candidates)
    {
        _manager = new WindowManager(new AppWindowCatalog());
        _fake = new FakeWatchlist(rows, candidates);
        Services.AddSingleton(_manager);
        Services.AddSingleton<IWatchlistQueries>(_fake);
        Services.AddSingleton<TimeProvider>(_clock);

        return Render<WatchlistPanel>();
    }

    private static Event Bridgestone(int id, string name) => new()
    {
        Id = id,
        Name = name,
        StartsAt = Now.AddDays(30),
        OnSaleAt = Now.AddDays(3),
        Venue = new Venue { Name = "Bridgestone Arena", City = "Nashville", State = "TN", Timezone = "America/Chicago", TicketingProvider = "ticketmaster" },
        Performer = new Performer { Name = "Example Performer" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    private static SourceQuote Quote(Source source, decimal? lowest, bool? allIn, int? listings = null, string? status = null)
        => new(source, lowest, allIn, listings, "USD", Now.AddMinutes(-12), status, false);

    private static WatchlistRow Row(Event evt, IEnumerable<SourceQuote> quotes, decimal? target = null, string? primaryStatus = null)
        => new(
            new Watch { Id = evt.Id, EventId = evt.Id, Event = evt, TargetPrice = target },
            evt,
            WatchlistRow.StateOf(evt, Now),
            MarketBest.Compose(SourceKind.Primary, quotes),
            MarketBest.Compose(SourceKind.Resale, quotes),
            primaryStatus,
            primaryStatus is null ? null : Now.AddMinutes(-5),
            PrimaryReappeared: false);

    [Fact]
    public void An_empty_watchlist_says_how_a_watch_comes_to_exist()
    {
        var cut = Render();

        var empty = cut.Find(".empty--new").TextContent;

        Assert.Contains("A watch is the instruction to poll", empty);
        Assert.Contains("nothing is fetched without", empty);
        Assert.Contains("live run seeds a watch", empty);
        Assert.Empty(cut.FindAll("tbody .mud-table-row"));
    }

    [Fact]
    public void A_row_renders_primary_and_resale_best_in_separate_cells_and_never_one_number_for_both()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt,
        [
            Quote(Ticketmaster, 59.5m, allIn: false, status: InventoryStatus.Available),
            Quote(SeatGeek, 84m, allIn: true, listings: 412),
            Quote(StubHub, 91m, allIn: true, listings: 40)
        ], target: 80m, primaryStatus: InventoryStatus.Available));

        var primary = cut.Find(".price[data-market=primary]");
        var resale = cut.Find(".price[data-market=resale]");

        Assert.Contains("$59.50", primary.TextContent);
        Assert.Contains("face value", primary.TextContent);
        Assert.Contains("Available", primary.TextContent);
        Assert.Equal("TM", primary.QuerySelector(".price__book")!.TextContent.Trim());
        Assert.DoesNotContain("$84.00", primary.TextContent);

        Assert.Contains("$84.00", resale.TextContent);
        Assert.Contains("all-in", resale.TextContent);
        Assert.Contains("412 listed", resale.TextContent);
        Assert.Equal("SG", resale.QuerySelector(".price__book")!.TextContent.Trim());
        Assert.DoesNotContain("$59.50", resale.TextContent);

        // Every price is a link to the source that published it, named on the link.
        var links = cut.FindAll("button.desklink").Where(l => l.GetAttribute("title")?.StartsWith("Open this event at", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.GetAttribute("title") == "Open this event at SeatGeek");
        Assert.Contains("Resale listings and prices from SeatGeek.", cut.Markup);

        // The face-value primary sits under the all-in target, and that says nothing about what
        // a buyer would pay: the resale all-in best is what the target is read against.
        Assert.DoesNotContain(cut.FindAll(".tag"), t => t.TextContent.Trim() == "Met");
    }

    [Fact]
    public void A_mixed_fee_basis_renders_no_number_and_says_why()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt,
        [
            Quote(SeatGeek, 84m, allIn: true, listings: 412),
            Quote(StubHub, 70m, allIn: false, listings: 40)
        ]));

        Assert.Empty(cut.FindAll(".price[data-market=resale]"));

        var gaps = cut.FindAll(".board__gap").Select(g => g.TextContent.Trim()).ToList();

        Assert.Contains("Not ranked: SeatGeek all-in against StubHub face value.", gaps);
        Assert.Contains("No primary source has reported.", gaps);
        Assert.DoesNotContain("$70.00", cut.Find(".mud-table-body").TextContent);
    }

    [Fact]
    public void The_spread_rail_has_one_tick_per_source_and_one_rail_per_market()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt,
        [
            Quote(Ticketmaster, 59.5m, allIn: false),
            Quote(SeatGeek, 84m, allIn: true),
            Quote(StubHub, 91m, allIn: true)
        ]));

        var primaryRail = cut.Find("[data-rail=primary] .rail");
        var resaleRail = cut.Find("[data-rail=resale] .rail");

        Assert.Equal(["ticketmaster"], primaryRail.QuerySelectorAll(".rail__tick").Select(t => t.GetAttribute("data-source")));
        Assert.Equal(["seatgeek", "stubhub"], resaleRail.QuerySelectorAll(".rail__tick").Select(t => t.GetAttribute("data-source")));

        // The best in each market is marked, and only within its own rail.
        Assert.Single(primaryRail.QuerySelectorAll(".rail__tick--best"));
        Assert.Equal("seatgeek", resaleRail.QuerySelector(".rail__tick--best")!.GetAttribute("data-source"));

        // Every tick names its source and its price.
        Assert.Contains("SG SeatGeek: $84.00 all-in", resaleRail.QuerySelector("[data-source=seatgeek]")!.GetAttribute("title"));

        // Each rail is badged with its market.
        Assert.Equal("Primary", cut.Find("[data-rail=primary] .tag").TextContent.Trim());
        Assert.Equal("Resale", cut.Find("[data-rail=resale] .tag").TextContent.Trim());
    }

    [Fact]
    public void Open_event_opens_the_event_window_about_that_event()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]));

        cut.Find("tbody .mud-table-row").Click();

        var open = cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Open event");
        open.Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event);
        Assert.Equal(7, window.Parameters["EventId"]);
        Assert.Equal("Example Tour", window.Title);
    }

    [Fact]
    public void The_event_name_is_itself_the_way_to_the_event_window()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, []));

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open event").Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Event);
        Assert.Equal(7, window.Parameters["EventId"]);
    }

    [Fact]
    public void The_follow_ups_open_about_the_event_too()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]));

        cut.Find("tbody .mud-table-row").Click();
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "All sources").Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.AllSources);
        Assert.Equal(7, window.Parameters["EventId"]);
    }

    [Fact]
    public void Trend_opens_about_the_event_too()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]));

        cut.Find("tbody .mud-table-row").Click();
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Trend").Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Trend);
        Assert.Equal(7, window.Parameters["EventId"]);
    }

    [Fact]
    public void The_performer_and_venue_names_open_their_destinations()
    {
        var evt = Bridgestone(7, "Example Tour");
        evt.PerformerId = 20;
        evt.Performer!.Id = 20;
        evt.VenueId = 30;
        evt.Venue!.Id = 30;

        var cut = Render(Row(evt, []));

        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open performer").Click();
        cut.FindAll("button.desklink").Single(l => l.GetAttribute("title") == "Open venue").Click();

        Assert.Equal(20, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Performer).Parameters["PerformerId"]);
        Assert.Equal(30, Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Venue).Parameters["VenueId"]);

        // Following a name is not opening the row: the actions stay hidden.
        Assert.Empty(cut.FindAll(".rowacts__keys"));
    }

    [Fact]
    public void The_metrics_count_watches_sellouts_and_reappearances()
    {
        var soldOut = Row(Bridgestone(1, "Sold out"), [Quote(Ticketmaster, null, null, status: InventoryStatus.NotAvailable)], primaryStatus: InventoryStatus.NotAvailable);
        var back = Row(Bridgestone(2, "Back"), [Quote(Ticketmaster, 59.5m, true)], primaryStatus: InventoryStatus.Available) with { PrimaryReappeared = true };
        var plain = Row(Bridgestone(3, "Plain"), [Quote(SeatGeek, 84m, true)]);

        var cut = Render(soldOut, back, plain);

        Assert.Equal("3", MetricValue(cut, "Watches"));
        Assert.Equal("1", MetricValue(cut, "Sold out on primary"));
        Assert.Equal("1", MetricValue(cut, "Reappeared"));

        // A sellout with no price still shows what the box office said.
        Assert.Contains("Not available", cut.Find("tbody .mud-table-row").TextContent);
    }

    [Fact]
    public void The_search_narrows_by_event_performer_or_venue_and_offers_the_way_back()
    {
        var cut = Render(
            Row(Bridgestone(1, "Alpha Tour"), []),
            Row(Bridgestone(2, "Beta Tour"), []));

        cut.Find("input").Input("beta");

        Assert.Single(cut.FindAll("tbody .mud-table-row"));

        cut.Find("input").Input("nobody");

        Assert.Contains("No watched event matches", cut.Find(".empty--filtered").TextContent);

        cut.Find(".empty--filtered button").Click();

        Assert.Equal(2, cut.FindAll("tbody .mud-table-row").Count);
    }

    [Fact]
    public void The_on_sale_state_reads_off_the_clock()
    {
        var evt = Bridgestone(7, "Example Tour");

        evt.OnSaleAt = Now.AddDays(1);
        Assert.Equal(OnSaleState.Upcoming, WatchlistRow.StateOf(evt, Now));

        evt.OnSaleAt = Now.AddMinutes(-30);
        Assert.Equal(OnSaleState.OnSaleNow, WatchlistRow.StateOf(evt, Now));

        evt.OnSaleAt = Now.AddHours(-3);
        Assert.Equal(OnSaleState.Opened, WatchlistRow.StateOf(evt, Now));

        evt.OnSaleTbd = true;
        Assert.Equal(OnSaleState.Unannounced, WatchlistRow.StateOf(evt, Now));
    }

    private static string MetricValue(IRenderedComponent<WatchlistPanel> cut, string label)
    {
        var tile = cut.FindAll(".metric")
            .Single(m => m.QuerySelector(".metric__label")?.TextContent.Trim() == label);

        return tile.QuerySelector(".metric__value")!.TextContent.Trim();
    }

    [Fact]
    public void Add_watches_lists_upcoming_events_and_a_press_watches_one()
    {
        var watched = Bridgestone(7, "Already Watched");
        var fresh = Bridgestone(8, "Fresh Tour");
        var cut = RenderWith([], [new WatchCandidate(watched, true), new WatchCandidate(fresh, false)]);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Add watches")).Click();

        var rows = cut.FindAll(".watch-picker__row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Watching", rows[0].TextContent);
        Assert.Empty(rows[0].QuerySelectorAll(".watch-picker__watch"));

        rows[1].QuerySelector(".watch-picker__watch")!.Click();

        Assert.Equal([8], _fake.Watched);
        cut.WaitForAssertion(() => Assert.Contains("Watching", cut.FindAll(".watch-picker__row")[1].TextContent));

        // The board is replaced while picking, and comes back when done.
        Assert.Empty(cut.FindAll(".deskgrid"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Done adding")).Click();
        Assert.Empty(cut.FindAll(".watch-picker"));
    }

    [Fact]
    public void An_empty_picker_says_where_events_come_from()
    {
        var cut = RenderWith([], []);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Add watches")).Click();

        Assert.Contains("daily discovery run", cut.Find(".watch-picker .empty").TextContent);
    }

    [Fact]
    public void A_picker_search_waits_for_the_typing_to_pause_and_says_when_nothing_matches()
    {
        var cut = RenderWith([], []);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Add watches")).Click();

        cut.Find(".watch-picker input").Input("nobody");

        // The query waits for the typing to pause, and the pause is the test's to give.
        Assert.DoesNotContain("nobody", _fake.Searches);
        _clock.Advance(TimeSpan.FromMilliseconds(250));

        cut.WaitForAssertion(() => Assert.Contains("No upcoming event matches", cut.Find(".watch-picker .empty").TextContent));
        Assert.Contains("nobody", _fake.Searches);
    }

    [Fact]
    public void Stop_watching_switches_the_watch_off()
    {
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]));

        cut.Find("tbody tr").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Stop watching")).Click();

        Assert.Equal([7], _fake.Unwatched);
    }

    [Fact]
    public void Stop_watching_says_how_many_fans_also_watch_and_asks_before_switching_theirs_off()
    {
        var alerts = new FakeAlerts { Answer = false };
        Services.AddSingleton<TicketMiser.Desk.Primitives.IDeskAlerts>(alerts);
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]) with { FanWatchers = 3 });

        cut.Find("tbody tr").Click();
        var stop = cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Contains("Stop watching"));
        Assert.Equal("Stop watching (3 fans also watch)", stop.TextContent.Trim());

        // Kept: the operator said no, so nothing is switched off.
        stop.Click();
        var asked = Assert.Single(alerts.Asked);
        Assert.Equal("Stop watching Example Tour for everyone?", asked.Heading);
        Assert.Contains("3 fans watch this event from their own accounts", asked.Message);
        Assert.Equal("Stop for everyone", asked.ConfirmLabel);
        Assert.Equal("Keep watching", asked.CancelLabel);
        Assert.True(asked.Destructive);
        Assert.Empty(_fake.Unwatched);

        // Confirmed: every watch on the event goes off, as the label said.
        alerts.Answer = true;
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Contains("Stop watching")).Click();
        Assert.Equal(2, alerts.Asked.Count);
        Assert.Equal([7], _fake.Unwatched);
    }

    [Fact]
    public void Stop_watching_an_event_no_fan_watches_asks_nothing()
    {
        var alerts = new FakeAlerts { Answer = false };
        Services.AddSingleton<TicketMiser.Desk.Primitives.IDeskAlerts>(alerts);
        var evt = Bridgestone(7, "Example Tour");
        var cut = Render(Row(evt, [Quote(Ticketmaster, 59.5m, allIn: true)]));

        cut.Find("tbody tr").Click();
        var stop = cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Contains("Stop watching"));
        Assert.Equal("Stop watching", stop.TextContent.Trim());
        stop.Click();

        Assert.Empty(alerts.Asked);
        Assert.Equal([7], _fake.Unwatched);
    }
}
