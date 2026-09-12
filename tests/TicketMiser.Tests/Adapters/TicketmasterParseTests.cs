using System.Text.Json;
using TicketMiser.Core.Contracts;
using TicketMiser.Ingestion.Adapters;

namespace TicketMiser.Tests.Adapters;

/// <summary>
/// Parsing is pinned against recorded payloads rather than a live call, so the suite stays
/// deterministic, offline and free. The Ticketmaster fixtures are real responses recorded
/// on 2026-09-11 with the key redacted; they corrected the documentation in three places,
/// and each of those corrections is a test here.
/// </summary>
public class TicketmasterParseTests
{
    internal static JsonElement Load(string source, string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", source, fixture);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_search_page_of_real_nashville_events_parses_every_row_and_prices_only_the_clubs()
    {
        var (events, observations) = TicketmasterDiscoveryAdapter.ParseEvents(Load("ticketmaster", "nashville-by-date.json"), ObservedAt);

        Assert.Equal(20, events.Count);

        // Eleven rows carry a price range, and every one of them is a TicketWeb club show. The
        // Live Nation arena and theatre events carry none, on sale or not.
        Assert.Equal(11, observations.Count);
        Assert.All(observations, o => Assert.StartsWith("rZ7Hn", o.SourceEventId));

        var basementEast = observations.Single(o => o.SourceEventId == "rZ7HnEZ1AfOwpd");
        Assert.Equal(27.32m, basementEast.Lowest);
        Assert.Equal(90.91m, basementEast.Highest);
        Assert.False(basementEast.AllIn);
    }

    [Fact]
    public void The_all_in_flag_is_read_from_the_ticketing_object_where_the_real_payload_puts_it()
    {
        var (events, _) = TicketmasterDiscoveryAdapter.ParseEvents(Load("ticketmaster", "nashville-by-date.json"), ObservedAt);

        var root = Load("ticketmaster", "nashville-by-date.json");
        var flagged = root.GetProperty("_embedded").GetProperty("events").EnumerateArray()
            .Count(TicketmasterDiscoveryAdapter.ReadAllInclusive);

        Assert.Equal(6, flagged);

        // Brooks & Dunn at Bridgestone says all-in; the same night's Juanes primary at the Ryman says not.
        var brooksDunn = Load("ticketmaster", "event-bridgestone-brooks-dunn.json");
        Assert.True(TicketmasterDiscoveryAdapter.ReadAllInclusive(brooksDunn));
        Assert.Contains(events, e => e.SourceEventId == "G5viZ_A7ur5ht");
    }

    [Fact]
    public void A_marketplace_listing_is_its_own_channel_and_its_placeholder_on_sale_is_null()
    {
        var (events, _) = TicketmasterDiscoveryAdapter.ParseEvents(Load("ticketmaster", "nashville-by-date.json"), ObservedAt);

        // The Ryman is an AXS room. Ticketmaster lists Juanes there twice: the primary, with
        // presales and a real on-sale, and its own marketplace, with a 1900 placeholder.
        var primary = events.Single(e => e.SourceEventId == "G5viZ_A7ur5ht");
        var marketplace = events.Single(e => e.SourceEventId == "Z7r9jZ1A7-F__");

        Assert.Equal(ListingChannel.Primary, primary.Channel);
        Assert.Equal(new DateTimeOffset(2026, 2, 20, 16, 0, 0, TimeSpan.Zero), primary.OnSaleAt);
        Assert.Equal(3, primary.PresaleWindows.Count);

        Assert.Equal(ListingChannel.Marketplace, marketplace.Channel);
        Assert.Equal(primary.Venue.Name, marketplace.Venue.Name);
        Assert.Equal(primary.StartsAt, marketplace.StartsAt);

        // Two marketplace rows on this page carry the 1900 placeholder; neither becomes a date.
        var placeholders = events.Where(e => e.Channel == ListingChannel.Marketplace && e.OnSaleAt is null).ToList();
        Assert.Equal(2, placeholders.Count);
        Assert.DoesNotContain(events, e => e.OnSaleAt is { } t && t.Year < 1970);

        var adapter = new TicketmasterDiscoveryAdapterKeys();
        Assert.Equal("ticketmaster", adapter.KeyFor(ListingChannel.Primary));
        Assert.Equal("ticketmaster-resale", adapter.KeyFor(ListingChannel.Marketplace));
    }

    [Fact]
    public void An_arena_event_detail_carries_the_sale_calendar_and_no_price_range()
    {
        var parsed = TicketmasterDiscoveryAdapter.ParseEvent(Load("ticketmaster", "event-bridgestone-brooks-dunn.json"), ObservedAt);

        Assert.NotNull(parsed);
        var (evt, observation) = parsed.Value;

        Assert.Equal("G5viZ_A30im87", evt.SourceEventId);
        Assert.Equal("Bridgestone Arena", evt.Venue.Name);
        Assert.Equal("Nashville", evt.Venue.City);
        Assert.Equal("TN", evt.Venue.State);
        Assert.Equal("America/Chicago", evt.Venue.Timezone);
        Assert.Equal("concert", evt.CategoryKey);
        Assert.Equal("onsale", evt.Status);
        Assert.Equal(new DateTimeOffset(2026, 2, 27, 16, 0, 0, TimeSpan.Zero), evt.OnSaleAt);
        Assert.Equal(7, evt.PresaleWindows.Count);
        Assert.Contains(evt.PresaleWindows, p => p.Name == "LIVE NATION PRESALE");

        // On sale for seven months, all-in pricing enabled, and no price range anywhere in the
        // payload. The Discovery API does not quote the Live Nation rooms.
        Assert.Null(observation);
    }

    [Fact]
    public void A_club_event_detail_carries_a_face_value_range_with_no_all_in_claim()
    {
        var parsed = TicketmasterDiscoveryAdapter.ParseEvent(Load("ticketmaster", "event-basement-east-club.json"), ObservedAt);

        Assert.NotNull(parsed);
        var (evt, observation) = parsed.Value;

        Assert.Equal("The Basement East", evt.Venue.Name);
        Assert.Equal(new DateTimeOffset(2026, 4, 9, 15, 0, 0, TimeSpan.Zero), evt.OnSaleAt);
        Assert.NotNull(observation);
        Assert.Equal(27.32m, observation!.Lowest);
        Assert.Equal(90.91m, observation.Highest);
        Assert.Equal("USD", observation.Currency);
        Assert.False(observation.AllIn);
    }

    [Fact]
    public void Upcoming_on_sales_are_real_friday_at_ten_central_dates()
    {
        var (events, _) = TicketmasterDiscoveryAdapter.ParseEvents(Load("ticketmaster", "nashville-future-onsales.json"), ObservedAt);

        var tso = events.Single(e => e.SourceEventId == "G5viZ_oinLsr4");
        Assert.Equal("Bridgestone Arena", tso.Venue.Name);

        // 2026-09-18 15:00Z is Friday 10:00 Central: the convention, in the data.
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero), tso.OnSaleAt);
        Assert.Equal(DayOfWeek.Friday, tso.OnSaleAt!.Value.DayOfWeek);
        Assert.Contains(tso.PresaleWindows, p => p.Name.Contains("Platinum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Availability_reads_status_and_resale_status_per_event()
    {
        var statuses = TicketmasterInventoryAdapter.Parse(Load("ticketmaster", "availability.json"));

        Assert.Equal(3, statuses.Count);
        var few = statuses.Single(s => s.SourceEventId == "Z7r9jZ1AdF9Ex");
        Assert.Equal("FEW_TICKETS_LEFT", few.PrimaryStatus);
        Assert.Equal("TICKETS_AVAILABLE", few.ResaleStatus);

        var unknown = statuses.Single(s => s.SourceEventId == "Z7r9jZ1AdUNK");
        Assert.Equal("UNKNOWN", unknown.PrimaryStatus);
        Assert.Null(unknown.ResaleStatus);
    }

    [Fact]
    public void The_feed_is_filtered_to_the_state_and_category_and_carries_on_sale_times()
    {
        var root = Load("ticketmaster", "feed.json");
        var query = new DiscoveryQuery("Nashville", "TN", "US", "concert",
            new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var kept = root.EnumerateArray()
            .Select(TicketmasterFeedAdapter.ParseEvent)
            .Where(e => e is not null && TicketmasterFeedAdapter.Matches(e, query))
            .Select(e => e!)
            .ToList();

        var only = Assert.Single(kept);
        Assert.Equal("Z7r9jZ1AdF9Ex", only.SourceEventId);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero), only.OnSaleAt);
        Assert.Equal("Bridgestone Arena", only.Venue.Name);
        Assert.Single(only.PresaleWindows);
    }

    /// <summary>The adapter's key mapping without an HttpClient: the static rule is what is under test.</summary>
    private sealed class TicketmasterDiscoveryAdapterKeys
    {
        public string KeyFor(ListingChannel channel)
            => channel == ListingChannel.Marketplace
                ? TicketmasterDiscoveryAdapter.MarketplaceSourceKey
                : TicketmasterDiscoveryAdapter.SourceKey;
    }
}
