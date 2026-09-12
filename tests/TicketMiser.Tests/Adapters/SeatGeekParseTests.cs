using TicketMiser.Ingestion.Adapters;

namespace TicketMiser.Tests.Adapters;

public class SeatGeekParseTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 18, 15, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Events_carry_stats_as_one_observation_each_and_never_claim_all_in()
    {
        var (events, observations) = SeatGeekAdapter.ParseEvents(TicketmasterParseTests.Load("seatgeek", "events.json"), ObservedAt);

        // The malformed row has no venue and is dropped.
        Assert.Equal(2, events.Count);

        var headliner = events.Single(e => e.SourceEventId == "6001234");
        Assert.Equal("Example Headliner", headliner.Name);
        Assert.Equal("Bridgestone Arena", headliner.Venue.Name);
        Assert.Equal("12", headliner.Venue.SourceVenueId);
        Assert.Equal("Nashville", headliner.Venue.City);
        Assert.Equal("TN", headliner.Venue.State);
        Assert.Equal("Example Headliner", headliner.Performer?.Name);
        Assert.Equal("55", headliner.Performer?.SourcePerformerId);
        Assert.Equal("concert", headliner.CategoryKey);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), headliner.StartsAt);

        // SeatGeek gives no on-sale time. The adapter must not invent one.
        Assert.Null(headliner.OnSaleAt);

        var priced = observations.Single(o => o.SourceEventId == "6001234");
        Assert.Equal(68m, priced.Lowest);
        Assert.Equal(138m, priced.Average);
        Assert.Equal(1250m, priced.Highest);
        Assert.Equal(412, priced.ListingCount);
        Assert.False(priced.AllIn);

        // No listings is a real observation with a null price and a zero count.
        var empty = observations.Single(o => o.SourceEventId == "6005678");
        Assert.Null(empty.Lowest);
        Assert.Equal(0, empty.ListingCount);
    }
}
