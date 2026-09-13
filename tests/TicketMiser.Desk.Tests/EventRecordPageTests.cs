using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The public page at /e/{slug}: the on-sale record a fan reads from a shared link. The record
/// service is faked, so the page is judged on what it says — that an unknown address is a
/// miss and not an empty record; that a record shows the metrics, the two charts and every
/// tick with a citation and a fee flag on each price; that SeatGeek is named where its
/// numbers are; and that the page says where a reader can take what they saw without ever
/// saying what it means (docs/legal-guidelines.md, rules 3 and 9).
/// </summary>
public class EventRecordPageTests : DeskTestContext
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };

    private const string Address = "example-performer-bridgestone-arena-2026-11-19";

    private static Event Bridgestone() => new()
    {
        Id = 7,
        Name = "Example Tour",
        Slug = Address,
        StartsAt = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
        OnSaleAt = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero),
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

    private IRenderedComponent<EventRecord> Open(OnSaleRecord? record, string slug = Address)
    {
        Services.AddSingleton<IOnSaleRecordService>(new FakeRecords(record));

        return RenderComponent<EventRecord>(p => p.Add(x => x.Slug, slug));
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

    private static OnSaleRecord Recorded(bool withSeatGeek = true)
    {
        var ticks = new List<OnSaleTick>
        {
            Tick(Ticketmaster, -15, InventoryStatus.Unknown),
            Tick(Ticketmaster, 0, InventoryStatus.Available, 59.5m, allIn: true),
            Tick(Ticketmaster, 5, InventoryStatus.FewLeft, 59.5m, allIn: true),
            Tick(Ticketmaster, 10, InventoryStatus.NotAvailable),
            Tick(Ticketmaster, 95, InventoryStatus.Available, 59.5m, allIn: true)
        };

        if (withSeatGeek)
        {
            ticks.Add(Tick(SeatGeek, -15, listings: 12, lowest: 210m, allIn: false));
            ticks.Add(Tick(SeatGeek, 10, listings: 412, lowest: 240m, allIn: false));
            ticks.Add(Tick(SeatGeek, 95, listings: 380, lowest: 199m, allIn: false));
        }

        Source[] sources = withSeatGeek ? [Ticketmaster, SeatGeek] : [Ticketmaster];

        return new OnSaleRecord(Bridgestone(), sources, ticks, OnSaleRecord.Derive(ticks, sources));
    }

    [Fact]
    public void An_unknown_slug_is_a_miss_not_an_empty_record()
    {
        var cut = Open(Recorded(), slug: "nope");

        Assert.Contains("No record at this address", cut.Find("h1").TextContent);
        Assert.Contains("No event on record has that address", cut.Find(".empty--error").TextContent);
        Assert.Empty(cut.FindAll(".metric"));
        Assert.Empty(cut.FindAll("figure.deskchart"));
    }

    [Fact]
    public void The_page_names_the_event_the_room_and_the_night_in_the_venue_zone()
    {
        var cut = Open(Recorded());

        Assert.Equal("Example Tour", cut.Find("h1").TextContent);

        var where = cut.Find(".record-page__where").TextContent;
        Assert.Contains("Example Performer", where);
        Assert.Contains("Bridgestone Arena, Nashville, TN", where);
        // 01:00 UTC on 20 November is the evening of the 19th in Nashville.
        Assert.Contains("Thu 19 Nov 2026 19:00 America/Chicago", where);
        Assert.Contains("on sale Fri 18 Sep 2026 10:00 America/Chicago", where);
    }

    [Fact]
    public void A_record_renders_the_metrics_the_two_charts_and_every_tick()
    {
        var cut = Open(Recorded());

        var metrics = cut.FindAll(".metric");
        Assert.Equal(4, metrics.Count);
        Assert.Equal("$59.50", metrics[0].QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Equal("all-in at Ticketmaster, T", metrics[0].QuerySelector(".metric__note")!.TextContent);
        Assert.Equal("T+10 min", metrics[1].QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Equal("T+95 min", metrics[2].QuerySelector(".metric__value")!.TextContent.Trim());
        Assert.Equal("412", metrics[3].QuerySelector(".metric__value")!.TextContent.Trim());

        var charts = cut.FindAll("figure.deskchart");
        Assert.Equal(2, charts.Count);
        Assert.Contains("Sold out T+10 min, face value back T+95 min", charts[0].QuerySelector(".deskchart__headline")!.TextContent);
        Assert.Contains("412 resale listings at the sellout minute", charts[1].QuerySelector(".deskchart__headline")!.TextContent);

        // A plain table, one row per tick, and nothing on the page that needs a circuit.
        Assert.Equal(8, cut.FindAll("table.ticks tbody tr").Count);
        Assert.Empty(cut.FindAll("button.desklink"));
        Assert.Empty(cut.FindAll("[role=tab]"));
    }

    [Fact]
    public void Every_price_cell_carries_its_source_link_and_its_fee_flag()
    {
        var cut = Open(Recorded());

        var cites = cut.FindAll("table.ticks a.cite");

        // Ticketmaster: 3 priced ticks × lowest and highest. SeatGeek: 3 ticks × lowest, highest and listings.
        Assert.Equal(15, cites.Count);
        Assert.All(cites, a => Assert.Contains("Open this event at", a.GetAttribute("title")));
        Assert.All(cites, a => Assert.Equal("_blank", a.GetAttribute("target")));
        Assert.All(cites, a => Assert.Contains("noopener", a.GetAttribute("rel")));
        Assert.Equal(6, cites.Count(a => a.GetAttribute("href") == "https://www.ticketmaster.com/event/tm-123"));
        Assert.Equal(9, cites.Count(a => a.GetAttribute("href") == "https://seatgeek.com/e/events/6543210"));

        // The fee status stands beside every price, never implied: one tag per priced tick.
        var fees = cut.FindAll("table.ticks .tag").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(3, fees.Count(f => f == "all-in"));
        Assert.Equal(3, fees.Count(f => f == "face value"));
    }

    [Fact]
    public void SeatGeek_is_named_and_linked_where_its_numbers_are()
    {
        var cut = Open(Recorded());

        var credit = cut.Find("p.attribution");

        Assert.Contains("Resale listings and prices from SeatGeek", credit.TextContent);
        Assert.Equal("https://seatgeek.com", credit.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void SeatGeek_is_not_named_where_it_reported_nothing()
    {
        var cut = Open(Recorded(withSeatGeek: false));

        Assert.Empty(cut.FindAll("p.attribution"));
        Assert.DoesNotContain("SeatGeek", cut.Find("main").TextContent);
    }

    [Fact]
    public void The_page_says_how_it_was_collected_and_where_to_take_it_without_alleging_anything()
    {
        var cut = Open(Recorded());

        var sections = cut.FindAll("section");
        Assert.Equal(2, sections.Count);

        var how = sections[0].TextContent;
        Assert.Contains("official API", how);
        Assert.Contains("Nothing was scraped", how);
        Assert.Contains("discarded", how);

        var where = sections[1];
        var links = where.QuerySelectorAll("a").Select(a => a.GetAttribute("href")!).ToList();
        Assert.Contains(links, l => l.StartsWith("https://www.tn.gov/attorneygeneral/", StringComparison.Ordinal));
        Assert.Contains("https://reportfraud.ftc.gov/", links);

        // Rule 9: the timeline and the forms, never a finding.
        var page = cut.Find("main").TextContent;
        Assert.DoesNotContain("violat", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("illegal", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broke the law", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_footer_dates_the_record_by_its_last_tick_and_names_its_address()
    {
        var cut = Open(Recorded());

        var footer = cut.Find("footer").TextContent;

        Assert.Contains("Last tick Fri 18 Sep 2026 11:35 America/Chicago (T+95 min)", footer);
        Assert.Contains("8 ticks from Ticketmaster and SeatGeek", footer);
        Assert.Contains($"/e/{Address}", footer);
    }
}
