using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The public calendar at /onsales: a subscribed reader sees a presale and a public on-sale
/// for one event, on the right day and at the right hour in Nashville time, each linked to
/// the event's record, with the feed address to subscribe to; and an empty calendar says
/// when it fills rather than showing nothing.
/// </summary>
public class OnSalesPageTests : DeskTestContext
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static Event CmaAwards() => new()
    {
        Id = 652,
        Name = "The 60th Annual CMA Awards",
        Slug = "cma-awards-bridgestone-arena-2026-11-18",
        Venue = new Venue { Name = "Bridgestone Arena", City = "Nashville", State = "TN", Timezone = "America/Chicago", TicketingProvider = "ticketmaster" },
        StartsAt = new DateTimeOffset(2026, 11, 19, 1, 0, 0, TimeSpan.Zero),
        OnSaleAt = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero),
        Presales = """[{"name":"Artist presale","startsAt":"2026-09-15T15:00:00+00:00","endsAt":"2026-09-17T03:59:00+00:00"}]"""
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeCalendar(IReadOnlyList<OnSaleEntry> entries) : IOnSaleCalendarService
    {
        public Task<IReadOnlyList<OnSaleEntry>> LoadAsync(DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(entries);
    }

    private IRenderedComponent<OnSales> Open(params Event[] events)
    {
        Services.AddSingleton<TimeProvider>(new FixedClock(Now));
        Services.AddSingleton<IOnSaleCalendarService>(new FakeCalendar(OnSaleCalendar.Build(events, Now, Now.AddDays(90))));

        return Render<OnSales>();
    }

    [Fact]
    public void A_presale_and_an_on_sale_for_one_event_show_on_their_days_in_nashville_time()
    {
        var cut = Open(CmaAwards());

        var days = cut.FindAll("section.day h2").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(2, days.Count);
        Assert.StartsWith("Tuesday 15 September 2026", days[0]);
        Assert.StartsWith("Friday 18 September 2026", days[1]);

        var rows = cut.FindAll(".onsale");
        Assert.Equal(2, rows.Count);

        // 15:00Z is 10:00 in Nashville in September; the hour on the page is the announced one.
        Assert.Equal("10:00", rows[0].QuerySelector(".onsale__time")!.TextContent);
        Assert.Equal("10:00", rows[1].QuerySelector(".onsale__time")!.TextContent);
        Assert.Contains("Presale", rows[0].QuerySelector(".tag")!.TextContent);
        Assert.Contains("Artist presale", rows[0].TextContent);
        Assert.Contains("On sale", rows[1].QuerySelector(".tag")!.TextContent);

        // Each row links the event's record and names the room.
        Assert.All(rows, r =>
        {
            var link = r.QuerySelector("a.onsale__event")!;
            Assert.Equal("/e/cma-awards-bridgestone-arena-2026-11-18", link.GetAttribute("href"));
            Assert.Contains("Bridgestone Arena", r.QuerySelector(".onsale__room")!.TextContent);
        });
    }

    [Fact]
    public void The_page_offers_the_feed_to_subscribe_to()
    {
        var cut = Open(CmaAwards());

        Assert.Equal("/onsales.ics", cut.Find("link[rel=alternate][type='text/calendar']").GetAttribute("href"));
        Assert.StartsWith("webcal://", cut.Find(".subscribe a").GetAttribute("href"));
        Assert.EndsWith("/onsales.ics", cut.Find(".subscribe a").GetAttribute("href"));
        Assert.Contains("/onsales.ics", cut.Find(".subscribe code").TextContent);
    }

    [Fact]
    public void An_empty_calendar_says_when_it_fills()
    {
        var cut = Open();

        Assert.Empty(cut.FindAll(".onsale"));
        Assert.Contains("No presale or on-sale has been announced", cut.Find(".empty").TextContent);
        Assert.Contains("daily discovery run", cut.Find(".empty").TextContent);
    }

    [Fact]
    public void The_page_says_where_the_times_come_from_and_which_rooms_are_missing()
    {
        var cut = Open(CmaAwards());

        var text = cut.Markup;
        Assert.Contains("Nothing is scraped", text);
        Assert.Contains("Ryman", text);
    }
}
