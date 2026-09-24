using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Components.Prices;
using TicketMiser.Web.Components.Records;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The price cells as rendered, with and without an affiliate programme configured
/// (TicketMiser.Tests.Entities.SourceLinkTests pins the link itself). Configured, every price
/// cell on the public record page and on the Watchlist leads through the programme's deep link
/// and none leads to the untagged page; the public page says so in plain words (rule 7) and the
/// desk's copy of the same record does not. Unconfigured, every cell is the canonical event page
/// and no disclosure renders.
/// </summary>
public class SourceLinkRenderTests : DeskTestContext
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };

    private const string TicketmasterCanonical = "https://www.ticketmaster.com/event/tm-123";
    private const string SeatGeekCanonical = "https://seatgeek.com/e/events/6543210";
    private const string TicketmasterTagged = "https://ticketmaster.evyy.net/c/1111111/222222/4272?u=https%3A%2F%2Fwww.ticketmaster.com%2Fevent%2Ftm-123";
    private const string SeatGeekTagged = "https://seatgeek.pxf.io/c/1111111/333333/5555?u=https%3A%2F%2Fseatgeek.com%2Fe%2Fevents%2F6543210";

    private static readonly DateTimeOffset OnSale = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    public SourceLinkRenderTests()
    {
        Services.AddSingleton<IAccountQueries>(new NobodyWatches());
        Services.AddSingleton<AntiforgeryStateProvider, AccountPageTests.FakeAntiforgery>();
        Services.AddSingleton(new WindowManager(new AppWindowCatalog()));
    }

    /// <summary>Both programmes on, with dummy ids.</summary>
    private void ConfigureAffiliates() => Services.Configure<AffiliateOptions>(o =>
    {
        o.Ticketmaster = new AffiliateProgram
        {
            Enabled = true,
            Template = "https://ticketmaster.evyy.net/c/{publisherId}/{adId}/{programId}?u={url}",
            PublisherId = "1111111",
            AdId = "222222",
            ProgramId = "4272"
        };
        o.SeatGeek = new AffiliateProgram
        {
            Enabled = true,
            Template = "https://seatgeek.pxf.io/c/{publisherId}/{adId}/{programId}?u={url}",
            PublisherId = "1111111",
            AdId = "333333",
            ProgramId = "5555"
        };
    });

    private sealed class NobodyWatches : IAccountQueries
    {
        public Task<AccountSummary?> SummaryAsync(int accountId, CancellationToken ct = default) => Task.FromResult<AccountSummary?>(null);

        public Task<bool> IsWatchingAsync(int accountId, int eventId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class OneRecord(OnSaleRecord record) : IOnSaleRecordService
    {
        public Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default) => Task.FromResult<OnSaleRecord?>(record);

        public Task<OnSaleRecord?> GetBySlugAsync(string slug, CancellationToken ct = default) => Task.FromResult<OnSaleRecord?>(record);

        public Task<string?> SlugForAsync(int eventId, CancellationToken ct = default) => Task.FromResult(record.Event.Slug);
    }

    private sealed class Board(WatchlistRow row) : IWatchlistQueries
    {
        public Task<IReadOnlyList<WatchlistRow>> LoadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WatchlistRow>>([row]);

        public Task<IReadOnlyList<WatchCandidate>> CandidatesAsync(string? search, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WatchCandidate>>([]);

        public Task WatchAsync(int eventId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnwatchAsync(int eventId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Event Bridgestone() => new()
    {
        Id = 7,
        Name = "Example Tour",
        Slug = "example-performer-bridgestone-arena-2026-11-19",
        StartsAt = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
        OnSaleAt = OnSale,
        Venue = new Venue { Name = "Bridgestone Arena", City = "Nashville", State = "TN", Timezone = "America/Chicago", TicketingProvider = "ticketmaster" },
        Performer = new Performer { Name = "Example Performer" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    private static OnSaleTick Tick(Source source, int minute, string? status = null, decimal? lowest = null, bool? allIn = null, int? listings = null) => new()
    {
        EventId = 7,
        SourceId = source.Id,
        MinutesFromOnSale = minute,
        ObservedAt = OnSale.AddMinutes(minute),
        PrimaryStatus = status,
        Lowest = lowest,
        Highest = lowest is { } l ? l * 2 : null,
        AllIn = allIn,
        ListingCount = listings
    };

    private static OnSaleRecord Recorded()
    {
        var ticks = new List<OnSaleTick>
        {
            Tick(Ticketmaster, 0, InventoryStatus.Available, 59.5m, allIn: true),
            Tick(Ticketmaster, 10, InventoryStatus.NotAvailable),
            Tick(Ticketmaster, 95, InventoryStatus.Available, 59.5m, allIn: true),
            Tick(SeatGeek, 10, listings: 412, lowest: 240m, allIn: false),
            Tick(SeatGeek, 95, listings: 380, lowest: 199m, allIn: false)
        };
        Source[] sources = [Ticketmaster, SeatGeek];
        return new OnSaleRecord(Bridgestone(), sources, ticks, OnSaleRecord.Derive(ticks, sources));
    }

    private static WatchlistRow Row()
    {
        var evt = Bridgestone();
        var now = DateTimeOffset.UtcNow;
        SourceQuote[] quotes =
        [
            new(Ticketmaster, 59.5m, true, null, "USD", now.AddMinutes(-12), null, false),
            new(SeatGeek, 199m, false, 380, "USD", now.AddMinutes(-12), null, false)
        ];

        return new WatchlistRow(
            new Watch { Id = 1, EventId = evt.Id, Event = evt },
            evt,
            WatchlistRow.StateOf(evt, now),
            MarketBest.Compose(SourceKind.Primary, quotes),
            MarketBest.Compose(SourceKind.Resale, quotes),
            null,
            null,
            PrimaryReappeared: false);
    }

    private IRenderedComponent<EventRecord> PublicRecord()
    {
        var record = Recorded();
        Services.AddSingleton<IOnSaleRecordService>(new OneRecord(record));
        return Render<EventRecord>(p => p.Add(x => x.Slug, record.Event.Slug));
    }

    /// <summary>Every price and listings cell on the public page: the anchors that cite a source.</summary>
    private static List<string> CellHrefs(IRenderedComponent<EventRecord> cut)
        => cut.FindAll("table.ticks a.cite").Select(a => a.GetAttribute("href")!).ToList();

    /// <summary>Clicks every price cell on the board and returns where each asked the browser to go.</summary>
    private List<string> BoardClicks()
    {
        Services.AddSingleton<IWatchlistQueries>(new Board(Row()));
        var cut = Render<WatchlistPanel>();

        List<AngleSharp.Dom.IElement> Cells() => cut.FindAll("button.desklink")
            .Where(b => b.GetAttribute("title")?.StartsWith("Open this event at", StringComparison.Ordinal) == true)
            .ToList();

        var count = Cells().Count;
        Assert.Equal(2, count);

        // Found again before each click: a click re-renders the board.
        for (var i = 0; i < count; i++)
            Cells()[i].Click();

        return JSInterop.Invocations.Where(i => i.Identifier == "open").Select(i => (string)i.Arguments[0]!).ToList();
    }

    [Fact]
    public void Configured_every_price_cell_on_the_public_record_is_tagged_and_none_is_plain()
    {
        ConfigureAffiliates();
        var cut = PublicRecord();

        var hrefs = CellHrefs(cut);
        Assert.NotEmpty(hrefs);
        Assert.All(hrefs, h => Assert.True(h == TicketmasterTagged || h == SeatGeekTagged, h));
        Assert.DoesNotContain(TicketmasterCanonical, hrefs);
        Assert.DoesNotContain(SeatGeekCanonical, hrefs);
        Assert.Contains(TicketmasterTagged, hrefs);
        Assert.Contains(SeatGeekTagged, hrefs);

        // Marked as paid links for crawlers, and disclosed in plain words for readers.
        Assert.All(cut.FindAll("table.ticks a.cite"), a => Assert.Contains("sponsored", a.GetAttribute("rel")));
        var disclosure = cut.Find("[data-disclosure=affiliate]");
        Assert.Contains("may earn TicketMiser a commission", disclosure.TextContent);
        Assert.Contains("costs you nothing extra", disclosure.TextContent);
    }

    [Fact]
    public void Unconfigured_every_price_cell_on_the_public_record_is_the_canonical_page_and_nothing_is_disclosed()
    {
        var cut = PublicRecord();

        var hrefs = CellHrefs(cut);
        Assert.NotEmpty(hrefs);
        Assert.All(hrefs, h => Assert.True(h == TicketmasterCanonical || h == SeatGeekCanonical, h));
        Assert.All(cut.FindAll("table.ticks a.cite"), a => Assert.DoesNotContain("sponsored", a.GetAttribute("rel")));
        Assert.Empty(cut.FindAll("[data-disclosure]"));
        Assert.DoesNotContain(AffiliateOptions.Disclosure, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_every_price_cell_on_the_watchlist_opens_the_tagged_link()
    {
        ConfigureAffiliates();

        var opened = BoardClicks();

        Assert.Equal(new[] { SeatGeekTagged, TicketmasterTagged }, opened.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Unconfigured_every_price_cell_on_the_watchlist_opens_the_canonical_page()
    {
        var opened = BoardClicks();

        Assert.Equal(new[] { SeatGeekCanonical, TicketmasterCanonical }, opened.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_shared_price_cell_opens_the_tagged_link_when_configured()
    {
        ConfigureAffiliates();

        var cut = Render<SourcePrice>(p => p
            .Add(x => x.Source, SeatGeek)
            .Add(x => x.Event, Bridgestone())
            .Add(x => x.Amount, 199m)
            .Add(x => x.AllIn, false));
        cut.Find("button.desklink").Click();

        Assert.Equal(SeatGeekTagged, JSInterop.Invocations.Single(i => i.Identifier == "open").Arguments[0]);
    }

    [Fact]
    public void The_desk_s_copy_of_the_record_opens_tagged_links_and_stays_quiet_about_them()
    {
        ConfigureAffiliates();

        var cut = Render<OnSaleRecordView>(p => p.Add(x => x.Record, Recorded()).Add(x => x.InDesk, true));

        Assert.Empty(cut.FindAll("[data-disclosure]"));
        cut.FindAll("button.desklink").First(b => b.GetAttribute("title") == "Open this event at SeatGeek").Click();
        Assert.Equal(SeatGeekTagged, JSInterop.Invocations.Single(i => i.Identifier == "open").Arguments[0]);
    }
}
