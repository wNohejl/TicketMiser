using TicketMiser.Core.Contracts;
using TicketMiser.Ingestion.Adapters;

namespace TicketMiser.Tests.Adapters;

/// <summary>
/// Real SeatGeek responses recorded on 2026-09-11 with the client id redacted. The finding
/// they pin is the one that matters most: a fresh client id gets an empty stats block on
/// every event, priced or not, under every auth mode. Until SeatGeek grants stats, the
/// adapter yields events, cross-references and no observations.
/// </summary>
public class SeatGeekParseTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Real_events_parse_with_venue_performer_and_category_and_yield_no_observation_while_stats_are_empty()
    {
        var (events, observations) = SeatGeekAdapter.ParseEvents(TicketmasterParseTests.Load("seatgeek", "nashville-popular.json"), ObservedAt);

        Assert.Equal(5, events.Count);

        // listing_count.gt=50 matched 283 events server-side, and every stats block came back {}.
        // An empty block is not a zero price; it is no observation.
        Assert.Empty(observations);

        var jonas = events.Single(e => e.SourceEventId == "18503892");
        Assert.Equal("Jonas Brothers with Franklin Jonas and Deleasa", jonas.Name);
        Assert.Equal("Bridgestone Arena", jonas.Venue.Name);
        Assert.Equal("Nashville", jonas.Venue.City);
        Assert.Equal("TN", jonas.Venue.State);
        Assert.Equal("America/Chicago", jonas.Venue.Timezone);
        Assert.Equal("concert", jonas.CategoryKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 1, 30, 0, TimeSpan.Zero), jonas.StartsAt);
        Assert.NotNull(jonas.Performer);

        // SeatGeek gives no on-sale time. The adapter must not invent one.
        Assert.All(events, e => Assert.Null(e.OnSaleAt));
    }

    [Fact]
    public void A_ticketmaster_id_on_a_seatgeek_event_becomes_a_cross_reference()
    {
        var (events, _) = SeatGeekAdapter.ParseEvents(TicketmasterParseTests.Load("seatgeek", "nashville-popular.json"), ObservedAt);

        var jonas = events.Single(e => e.SourceEventId == "18503892");
        // The legacy host id, which the Discovery API cannot fetch, under its own key.
        Assert.Equal("1B00650D13C6A27C", jonas.KnownAs[ExternalIdKeys.TicketmasterLegacy]);
        Assert.False(jonas.KnownAs.ContainsKey("ticketmaster"));

        // The rest carry none, and say so with an empty map rather than a null.
        var rodrigo = events.Single(e => e.SourceEventId == "18211705");
        Assert.Empty(rodrigo.KnownAs);
    }

    [Fact]
    public void The_first_page_of_tonight_lists_the_ryman_which_ticketmaster_only_has_as_a_marketplace()
    {
        var (events, _) = SeatGeekAdapter.ParseEvents(TicketmasterParseTests.Load("seatgeek", "nashville-concerts-page.json"), ObservedAt);

        Assert.Equal(5, events.Count);
        var ryman = events.Single(e => e.SourceEventId == "18169296");
        Assert.Equal("Ryman Auditorium", ryman.Venue.Name);
        Assert.Equal("292", ryman.Venue.SourceVenueId);
    }
}
