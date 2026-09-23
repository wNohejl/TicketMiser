using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The history the Price history window draws: one list per market and never across them, one
/// line per source and fee basis, a source that changed basis split and flagged, the on-sale
/// record folded in without its once-a-minute repeats, the final price kept as the last point,
/// a price of unknown basis counted and left off, and purchases marked on their own market.
/// </summary>
public class PriceHistoryTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };
    private static readonly Source StubHub = new() { Id = 3, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale };
    private static readonly Source Feed = new() { Id = 4, Key = "ticketmaster-feed", Name = "Discovery Feed", Kind = SourceKind.Feed };

    private static readonly Dictionary<int, Source> Sources = new[] { Ticketmaster, SeatGeek, StubHub, Feed }.ToDictionary(s => s.Id);

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);

    private static readonly Event Show = new()
    {
        Id = 7,
        Name = "Example Tour",
        StartsAt = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
        Venue = new Venue { Name = "Bridgestone Arena", Timezone = "America/Chicago" }
    };

    private static PriceObservation Obs(Source s, int day, decimal lowest, bool allIn, int eventId = 7) => new()
    {
        EventId = eventId,
        SourceId = s.Id,
        ObservedAt = T0.AddDays(day),
        Lowest = lowest,
        AllIn = allIn
    };

    private static OnSaleTick Tick(Source s, int minute, decimal? lowest, bool? allIn) => new()
    {
        EventId = 7,
        SourceId = s.Id,
        ObservedAt = T0.AddDays(2).AddMinutes(minute),
        MinutesFromOnSale = minute,
        Lowest = lowest,
        AllIn = allIn
    };

    private static PriceHistory Compose(
        IEnumerable<PriceObservation>? observations = null,
        IEnumerable<OnSaleTick>? ticks = null,
        IEnumerable<FinalPrice>? finals = null,
        IEnumerable<Purchase>? purchases = null)
        => PriceHistory.Compose(Show, observations ?? [], ticks ?? [], finals ?? [], purchases ?? [], Sources);

    [Fact]
    public void The_two_markets_are_composed_apart_and_a_feed_is_never_a_line()
    {
        var history = Compose(
        [
            Obs(Ticketmaster, 0, 59.5m, allIn: true),
            Obs(SeatGeek, 0, 84m, allIn: true),
            Obs(StubHub, 1, 90m, allIn: true),
            Obs(Feed, 0, 1m, allIn: true)
        ]);

        Assert.Equal(["Ticketmaster"], history.Primary.Select(s => s.Source.Name));
        Assert.Equal(["SeatGeek", "StubHub"], history.Resale.Select(s => s.Source.Name));
        Assert.All(history.Primary, s => Assert.Equal(SourceKind.Primary, s.Kind));
        Assert.All(history.Resale, s => Assert.Equal(SourceKind.Resale, s.Kind));
        Assert.DoesNotContain(history.Sources, s => s.Kind == SourceKind.Feed);
    }

    [Fact]
    public void A_source_that_changes_basis_is_split_into_two_flagged_lines_each_named_for_its_basis()
    {
        var history = Compose(
        [
            Obs(SeatGeek, 0, 70m, allIn: false),
            Obs(SeatGeek, 1, 72m, allIn: false),
            Obs(SeatGeek, 2, 88m, allIn: true),
            Obs(StubHub, 0, 90m, allIn: true)
        ]);

        var seatGeek = history.Resale.Where(s => s.Source.Id == SeatGeek.Id).ToList();

        Assert.Equal(2, seatGeek.Count);
        Assert.All(seatGeek, s => Assert.True(s.BasisChanged));
        Assert.Equal(["SeatGeek · all-in", "SeatGeek · face value"], seatGeek.Select(s => s.Name));
        Assert.Equal([70m, 72m], seatGeek.Single(s => !s.AllIn).Points.Select(p => p.Lowest));
        Assert.Equal([88m], seatGeek.Single(s => s.AllIn).Points.Select(p => p.Lowest));

        Assert.False(history.Resale.Single(s => s.Source.Id == StubHub.Id).BasisChanged);
    }

    [Fact]
    public void Ticks_fold_in_without_their_repeats_and_a_tick_of_unknown_basis_is_counted_and_left_off()
    {
        var history = Compose(
            observations: [Obs(Ticketmaster, 0, 59.5m, allIn: true)],
            ticks:
            [
                Tick(Ticketmaster, 0, 59.5m, true),
                Tick(Ticketmaster, 1, 59.5m, true),
                Tick(Ticketmaster, 2, 65m, true),
                Tick(Ticketmaster, 3, 65m, true),
                Tick(Ticketmaster, 4, null, true),
                Tick(SeatGeek, 0, 200m, null)
            ]);

        var line = Assert.Single(history.Primary);
        Assert.Equal([59.5m, 65m], line.Points.Select(p => p.Lowest));
        Assert.Equal([PricePointOrigin.Observation, PricePointOrigin.OnSaleTick], line.Points.Select(p => p.Origin));

        Assert.Empty(history.Resale);
        Assert.Equal(1, history.UnstatedReadings);
    }

    [Fact]
    public void The_final_price_is_the_last_point_even_when_it_repeats_the_one_before()
    {
        var history = Compose(
            observations: [Obs(SeatGeek, 0, 84m, allIn: true), Obs(SeatGeek, 5, 80m, allIn: true)],
            finals: [new FinalPrice { EventId = 7, SourceId = SeatGeek.Id, Lowest = 80m, AllIn = true, ObservedAt = T0.AddDays(6) }]);

        var line = Assert.Single(history.Resale);

        Assert.Equal(3, line.Points.Count);
        Assert.Equal(PricePointOrigin.FinalPrice, line.Latest.Origin);
        Assert.Equal(80m, line.Low);
        Assert.Equal(84m, line.High);
    }

    [Fact]
    public void Readings_for_another_event_are_ignored()
    {
        var history = Compose([Obs(SeatGeek, 0, 84m, allIn: true, eventId: 8)]);

        Assert.True(history.IsEmpty);
    }

    [Fact]
    public void A_purchase_marks_its_own_market_and_one_bought_elsewhere_marks_both()
    {
        var history = Compose(
            observations: [Obs(SeatGeek, 0, 84m, allIn: true)],
            purchases:
            [
                new Purchase { EventId = 7, SourceId = SeatGeek.Id, PaidPerTicket = 90m, PurchasedAt = T0.AddDays(3) },
                new Purchase { EventId = 7, SourceId = null, PaidPerTicket = 75m, PurchasedAt = T0.AddDays(1) },
                new Purchase { EventId = 8, SourceId = SeatGeek.Id, PaidPerTicket = 1m, PurchasedAt = T0 }
            ]);

        Assert.Equal(2, history.Purchases.Count);

        var elsewhere = history.Purchases[0];
        Assert.Null(elsewhere.Source);
        Assert.True(elsewhere.Marks(SourceKind.Primary));
        Assert.True(elsewhere.Marks(SourceKind.Resale));

        var seatGeek = history.Purchases[1];
        Assert.Equal(SourceKind.Resale, seatGeek.Kind);
        Assert.False(seatGeek.Marks(SourceKind.Primary));
        Assert.True(seatGeek.Marks(SourceKind.Resale));
    }

    [Fact]
    public void The_axis_is_daily_over_a_long_span_and_each_bucket_holds_the_last_reading()
    {
        var history = Compose(
        [
            Obs(SeatGeek, 0, 84m, allIn: true),
            Obs(SeatGeek, 4, 80m, allIn: true),
            Obs(StubHub, 2, 95m, allIn: true)
        ]);

        var axis = TimeAxis.Over(history.Resale, "America/Chicago");

        Assert.Equal(TimeSpan.FromDays(1), axis.Step);
        Assert.Equal(5, axis.Count);

        // 15:00 UTC is 10:00 in Nashville; the buckets are Nashville's days.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-5)), axis.Starts[0]);

        Assert.Equal([84d, 84d, 84d, 84d, 80d], axis.Sample(history.Resale.Single(s => s.Source.Id == SeatGeek.Id)));

        // A line that starts late repeats its first reading back to the axis start: the chart
        // cannot draw a gap, and a zero would read as free tickets.
        Assert.Equal([95d, 95d, 95d, 95d, 95d], axis.Sample(history.Resale.Single(s => s.Source.Id == StubHub.Id)));

        Assert.Equal(4, axis.IndexOf(T0.AddDays(4).AddHours(2)));
        Assert.Equal(-1, axis.IndexOf(T0.AddDays(10)));
    }

    [Fact]
    public void The_axis_is_hourly_over_a_short_span_and_empty_with_no_readings()
    {
        var history = Compose(
            ticks: [Tick(Ticketmaster, 0, 59.5m, true), Tick(Ticketmaster, 150, 70m, true)]);

        var axis = TimeAxis.Over(history.Primary, "America/Chicago");

        Assert.Equal(TimeSpan.FromHours(1), axis.Step);
        Assert.Equal(3, axis.Count);
        Assert.Equal([59.5d, 59.5d, 70d], axis.Sample(history.Primary[0]));

        Assert.Equal(0, TimeAxis.Over([], "America/Chicago").Count);
    }
}
