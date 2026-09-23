using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The per-market composition the board reads: one best number per market chosen by
/// <see cref="PriceComparison"/>, a reason whenever there is none, and a rail with one tick per
/// source placed by price. Never a number across markets.
/// </summary>
public class MarketBestTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };
    private static readonly Source StubHub = new() { Id = 3, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale };
    private static readonly Source Feed = new() { Id = 4, Key = "ticketmaster-feed", Name = "Discovery Feed", Kind = SourceKind.Feed };

    private static SourceQuote Quote(Source source, decimal? lowest, bool? allIn, bool fromTick = false)
        => new(source, lowest, allIn, null, "USD", DateTimeOffset.UnixEpoch, null, fromTick);

    [Fact]
    public void Each_market_is_composed_from_its_own_quotes_only()
    {
        var quotes = new[]
        {
            Quote(Ticketmaster, 59.5m, true),
            Quote(SeatGeek, 40m, true),
            Quote(StubHub, 45m, true)
        };

        var primary = MarketBest.Compose(SourceKind.Primary, quotes);
        var resale = MarketBest.Compose(SourceKind.Resale, quotes);

        // The cheaper resale ask is never the primary market's best, however much cheaper.
        Assert.Equal(Ticketmaster.Id, primary.Best!.Source.Id);
        Assert.Equal(59.5m, primary.Best.Lowest);
        Assert.Single(primary.Rail);

        Assert.Equal(SeatGeek.Id, resale.Best!.Source.Id);
        Assert.Equal(40m, resale.Best.Lowest);
        Assert.Equal(2, resale.Rail.Count);
        Assert.Equal(5m, resale.Spread);
    }

    [Fact]
    public void A_mixed_fee_basis_yields_no_number_and_names_which_source_is_on_which()
    {
        var resale = MarketBest.Compose(SourceKind.Resale,
        [
            Quote(SeatGeek, 84m, true),
            Quote(StubHub, 70m, false)
        ]);

        Assert.Null(resale.Best);
        Assert.Null(resale.Spread);
        Assert.Equal("Not ranked: SeatGeek all-in against StubHub face value.", resale.Reason);

        // The rail still shows both, unranked: one tick per source is the rule, and nothing
        // is marked best.
        Assert.Equal(2, resale.Rail.Count);
        Assert.All(resale.Rail, t => Assert.False(t.IsBest));
    }

    [Fact]
    public void The_rail_places_each_source_by_price_between_the_best_and_the_worst()
    {
        var resale = MarketBest.Compose(SourceKind.Resale,
        [
            Quote(StubHub, 100m, true),
            Quote(SeatGeek, 80m, true),
            Quote(new Source { Id = 5, Key = "vivid", Name = "Vivid Seats", Kind = SourceKind.Resale }, 90m, true)
        ]);

        var ticks = resale.Rail.Select(t => (t.Monogram, t.Position, t.IsBest)).ToList();

        Assert.Equal(("SG", 0d, true), ticks[0]);
        Assert.Equal(("VS", 50d, false), ticks[1]);
        Assert.Equal(("SH", 100d, false), ticks[2]);
    }

    [Fact]
    public void Sources_that_agree_collapse_to_one_mark()
    {
        var resale = MarketBest.Compose(SourceKind.Resale,
        [
            Quote(SeatGeek, 80m, true),
            Quote(StubHub, 80m, true)
        ]);

        Assert.All(resale.Rail, t => Assert.Equal(0d, t.Position));
        Assert.Equal(0m, resale.Spread);
    }

    [Fact]
    public void An_empty_market_and_an_unpriced_one_each_say_so()
    {
        Assert.Equal("No primary source has reported.", MarketBest.Compose(SourceKind.Primary, []).Reason);

        var reported = MarketBest.Compose(SourceKind.Primary, [Quote(Ticketmaster, null, null, fromTick: true)]);

        Assert.Null(reported.Best);
        Assert.Equal("Ticketmaster reported without a price.", reported.Reason);
        Assert.Single(reported.Rail);
    }

    [Fact]
    public void A_price_with_an_unstated_fee_basis_is_never_ranked()
    {
        var alone = MarketBest.Compose(SourceKind.Primary, [Quote(Ticketmaster, 59.5m, null, fromTick: true)]);

        Assert.Null(alone.Best);
        Assert.Equal("Ticketmaster gave a price without saying whether fees are included.", alone.Reason);

        // Beside a stated one it neither wins nor blocks: the stated quote is the market's best
        // and the unstated tick sits at the far end of the rail, unmarked.
        var beside = MarketBest.Compose(SourceKind.Resale,
        [
            Quote(SeatGeek, 84m, true),
            Quote(StubHub, 10m, null, fromTick: true)
        ]);

        Assert.Equal(SeatGeek.Id, beside.Best!.Source.Id);
        Assert.Equal(100d, beside.Rail.Single(t => t.Quote.Source.Id == StubHub.Id).Position);
        Assert.Null(beside.Spread);
    }

    [Fact]
    public void A_feed_is_neither_a_market_nor_a_quote_in_one()
    {
        Assert.Throws<ArgumentException>(() => MarketBest.Compose(SourceKind.Feed, []));

        var primary = MarketBest.Compose(SourceKind.Primary, [Quote(Feed, 1m, true), Quote(Ticketmaster, 59.5m, true)]);

        Assert.Single(primary.Rail);
        Assert.Equal(Ticketmaster.Id, primary.Best!.Source.Id);
    }

    [Fact]
    public void Monograms_are_typographic_and_fall_back_to_initials()
    {
        Assert.Equal("TM", SourceMonogram.For(Ticketmaster));
        Assert.Equal("SG", SourceMonogram.For(SeatGeek));
        Assert.Equal("VS", SourceMonogram.For("vivid", "Vivid Seats"));
        Assert.Equal("AX", SourceMonogram.For("axs", "AXS"));
        Assert.Equal("ET", SourceMonogram.For("etix", "Etix"));
    }
}
