using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The on-sale calendar and its iCalendar feed, built from what the feed recorded on the
/// events: a presale and a public on-sale for one event land as two entries in order, in the
/// venue's zone; an on-sale still to be announced is not an entry; and the feed is a calendar
/// a strict client will accept — UTC stamps, escaped text, lines folded at 75 octets.
/// </summary>
public class OnSaleCalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Venue Bridgestone = new()
    {
        Name = "Bridgestone Arena",
        City = "Nashville",
        State = "TN",
        Timezone = "America/Chicago",
        TicketingProvider = "ticketmaster"
    };

    /// <summary>Friday 18 September 2026, 10:00 Nashville, is 15:00Z; the artist presale opens Tuesday at the same hour.</summary>
    private static Event CmaAwards(Action<Event>? tweak = null)
    {
        var evt = new Event
        {
            Id = 652,
            Name = "The 60th Annual CMA Awards",
            Slug = "cma-awards-bridgestone-arena-2026-11-18",
            Venue = Bridgestone,
            Performer = new Performer { Name = "CMA Awards" },
            StartsAt = new DateTimeOffset(2026, 11, 19, 1, 0, 0, TimeSpan.Zero),
            OnSaleAt = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero),
            Presales = """[{"name":"Artist presale","startsAt":"2026-09-15T15:00:00+00:00","endsAt":"2026-09-17T03:59:00+00:00"}]"""
        };
        tweak?.Invoke(evt);
        return evt;
    }

    [Fact]
    public void A_presale_and_a_public_on_sale_are_two_entries_in_order_in_the_venue_zone()
    {
        var entries = OnSaleCalendar.Build([CmaAwards()], Now, Now.AddDays(90));

        Assert.Equal(2, entries.Count);

        var presale = entries[0];
        Assert.Equal(OnSaleEntryKind.Presale, presale.Kind);
        Assert.Equal("Artist presale", presale.PresaleName);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero), presale.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 3, 59, 0, TimeSpan.Zero), presale.EndsAt);

        var open = entries[1];
        Assert.Equal(OnSaleEntryKind.Public, open.Kind);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero), open.StartsAt);

        // Nashville is Central: 15:00Z is 10:00 local in September.
        var local = OnSaleCalendar.InZone(open.StartsAt, open.Zone);
        Assert.Equal(new DateTime(2026, 9, 18, 10, 0, 0), local.DateTime);
        Assert.Equal("America/Chicago", open.Zone);

        Assert.Equal("On sale: The 60th Annual CMA Awards at Bridgestone Arena", open.Summary);
        Assert.Equal("Presale (Artist presale): The 60th Annual CMA Awards at Bridgestone Arena", presale.Summary);
    }

    [Fact]
    public void An_on_sale_still_to_be_announced_is_not_an_entry_and_a_window_bounds_the_list()
    {
        var tbd = CmaAwards(e => { e.Id = 1; e.OnSaleTbd = true; e.Presales = "[]"; });
        var past = CmaAwards(e => { e.Id = 2; e.OnSaleAt = Now.AddDays(-2); e.Presales = "[]"; });
        var far = CmaAwards(e => { e.Id = 3; e.OnSaleAt = Now.AddDays(120); e.Presales = "[]"; });
        var unreadable = CmaAwards(e => { e.Id = 4; e.OnSaleAt = null; e.Presales = "not json"; });

        var entries = OnSaleCalendar.Build([tbd, past, far, unreadable, CmaAwards()], Now, Now.AddDays(90));

        Assert.All(entries, e => Assert.Equal(652, e.Event.Id));
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Two_sales_at_the_same_minute_are_ordered_by_name()
    {
        var a = CmaAwards(e => { e.Id = 10; e.Name = "Zed"; e.Presales = "[]"; });
        var b = CmaAwards(e => { e.Id = 11; e.Name = "Alpha"; e.Presales = "[]"; });

        var entries = OnSaleCalendar.Build([a, b], Now, Now.AddDays(90));

        Assert.Equal(["Alpha", "Zed"], entries.Select(e => e.Event.Name));
    }

    [Fact]
    public void The_feed_is_a_calendar_with_one_event_per_entry_stamped_in_utc()
    {
        var entries = OnSaleCalendar.Build([CmaAwards()], Now, Now.AddDays(90));

        var ics = OnSaleIcs.Write(entries, "Nashville on-sales", "https://ticketmiser.example", Now);
        var lines = ics.Split("\r\n");

        Assert.Equal("BEGIN:VCALENDAR", lines[0]);
        Assert.Contains("VERSION:2.0", lines);
        Assert.Contains("X-WR-CALNAME:Nashville on-sales", lines);
        Assert.Equal(2, lines.Count(l => l == "BEGIN:VEVENT"));
        Assert.Equal(2, lines.Count(l => l == "END:VEVENT"));
        Assert.Equal("END:VCALENDAR", lines[^2]);

        // The public on-sale: 10:00 Nashville written as 15:00Z, a quarter-hour block, its record linked.
        Assert.Contains("DTSTART:20260918T150000Z", lines);
        Assert.Contains("DTEND:20260918T151500Z", lines);
        Assert.Contains("UID:event-652-onsale@ticketmiser", lines);
        Assert.Contains("URL:https://ticketmiser.example/e/cma-awards-bridgestone-arena-2026-11-18", lines);
        Assert.Contains("DTSTAMP:20260913T120000Z", lines);

        // The presale keeps the source's own end.
        Assert.Contains("DTSTART:20260915T150000Z", lines);
        Assert.Contains("DTEND:20260917T035900Z", lines);
        Assert.Contains(lines, l => l.StartsWith("UID:event-652-presale-artist-presale-", StringComparison.Ordinal));

        // The description repeats the hour in Nashville time, so a reader elsewhere still sees the announced hour.
        Assert.Contains("Public on-sale opens Fri 18 Sep 2026 10:00 America/Chicago.", ics);
        Assert.Contains("LOCATION:Bridgestone Arena\\, Nashville\\, TN", lines);
    }

    [Fact]
    public void Text_is_escaped_and_long_lines_are_folded_at_75_octets()
    {
        var evt = CmaAwards(e => { e.Name = "A very long name; with a comma, and more words than fit on one seventy-five octet line of an iCalendar file"; e.Presales = "[]"; });

        var ics = OnSaleIcs.Write(OnSaleCalendar.Build([evt], Now, Now.AddDays(90)), "Nashville on-sales", "https://x", Now);
        var lines = ics.Split("\r\n");

        Assert.All(lines, l => Assert.True(System.Text.Encoding.UTF8.GetByteCount(l) <= 75, l));

        var summary = string.Concat(lines.SkipWhile(l => !l.StartsWith("SUMMARY:", StringComparison.Ordinal))
            .TakeWhile((l, i) => i == 0 || l.StartsWith(' '))
            .Select((l, i) => i == 0 ? l : l[1..]));

        Assert.Equal("SUMMARY:On sale: A very long name\\; with a comma\\, and more words than fit on one seventy-five octet line of an iCalendar file at Bridgestone Arena", summary);
    }

    [Fact]
    public void Presales_are_read_through_one_parser_and_a_bad_column_is_an_empty_list()
    {
        Assert.Empty(EventPresale.Parse(null));
        Assert.Empty(EventPresale.Parse("[]"));
        Assert.Empty(EventPresale.Parse("{not json"));

        var one = Assert.Single(EventPresale.Parse("""[{"name":"Fan club","startsAt":"2026-09-15T15:00:00Z","endsAt":null}]"""));
        Assert.Equal("Fan club", one.Name);
        Assert.Null(one.EndsAt);
    }
}
