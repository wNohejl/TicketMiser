using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The Trend follow-up's numbers: each market's daily lowest, per fee basis, the cheapest reading
/// any source on that basis held at each day's end. Never a face-value number against an all-in
/// one, never a primary number against a resale one, and never a price after the doors opened.
/// </summary>
public class PriceTrendTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };
    private static readonly Source StubHub = new() { Id = 3, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale };

    private static readonly Dictionary<int, Source> Sources = new[] { Ticketmaster, SeatGeek, StubHub }.ToDictionary(s => s.Id);

    // 17:00 UTC is noon in Nashville, so a reading's day is the same day on both calendars.
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 17, 0, 0, TimeSpan.Zero);

    private static Event Show(DateTimeOffset startsAt) => new()
    {
        Id = 7,
        Name = "Example Tour",
        StartsAt = startsAt,
        Venue = new Venue { Name = "Bridgestone Arena", Timezone = "America/Chicago" }
    };

    private static PriceObservation Obs(Source s, int daysAgo, decimal lowest, bool allIn) => new()
    {
        EventId = 7,
        SourceId = s.Id,
        ObservedAt = Now.AddDays(-daysAgo),
        Lowest = lowest,
        AllIn = allIn
    };

    private static PriceTrend Trend(Event evt, params PriceObservation[] observations)
        => PriceTrend.Compose(PriceHistory.Compose(evt, observations, [], [], [], Sources), Now);

    [Fact]
    public void Each_day_carries_the_markets_lowest_on_one_basis_and_the_source_holding_it()
    {
        var trend = Trend(Show(Now.AddDays(30)),
            Obs(SeatGeek, 10, 90m, allIn: true),
            Obs(StubHub, 9, 85m, allIn: true),
            Obs(SeatGeek, 3, 80m, allIn: true),
            Obs(Ticketmaster, 20, 59.5m, allIn: false));

        var resale = Assert.Single(trend.Resale);
        Assert.True(resale.AllIn);
        Assert.Equal(PriceTrend.DefaultDays, resale.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 29), resale.Days[^1]);

        Assert.Equal(80m, resale.Now);
        Assert.Equal("SeatGeek", resale.NowHeldBy!.Name);

        // A week ago StubHub's 85 was the lowest all-in ask on the market.
        Assert.Equal(85m, resale.WeekAgo);
        Assert.Equal("StubHub", resale.HeldBy[^8]!.Name);
        Assert.Equal(-5m, resale.WeekChange);

        Assert.Equal((80m, new DateOnly(2026, 9, 26)), resale.Floor);

        // Nothing before the first reading, rather than a fabricated price.
        Assert.Null(resale.Lowest[0]);
        Assert.Equal(PriceTrend.DefaultDays - 11, resale.FirstReported);

        var primary = Assert.Single(trend.Primary);
        Assert.False(primary.AllIn);
        Assert.Equal(59.5m, primary.Now);
    }

    [Fact]
    public void A_face_value_line_and_an_all_in_line_in_one_market_are_two_lines()
    {
        var trend = Trend(Show(Now.AddDays(30)),
            Obs(SeatGeek, 5, 70m, allIn: false),
            Obs(StubHub, 5, 95m, allIn: true));

        Assert.Equal(2, trend.Resale.Count);
        Assert.Equal(95m, trend.Resale.Single(l => l.AllIn).Now);
        Assert.Equal(70m, trend.Resale.Single(l => !l.AllIn).Now);
    }

    [Fact]
    public void Once_the_event_has_started_the_trend_ends_at_its_start()
    {
        var start = Now.AddDays(-5);
        var trend = Trend(Show(start),
            Obs(SeatGeek, 8, 90m, allIn: true),
            Obs(SeatGeek, 2, 20m, allIn: true));

        var resale = Assert.Single(trend.Resale);

        Assert.Equal(DateOnly.FromDateTime(OnSaleCalendar.InZone(start, "America/Chicago").DateTime), trend.Through);
        Assert.Equal(90m, resale.Now);
        Assert.DoesNotContain(20m, resale.Lowest);
    }

    [Fact]
    public void An_event_with_no_readings_has_an_empty_trend()
    {
        var trend = Trend(Show(Now.AddDays(30)));

        Assert.True(trend.IsEmpty);
    }
}
