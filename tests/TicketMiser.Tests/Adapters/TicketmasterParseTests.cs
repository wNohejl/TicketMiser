using System.Text.Json;
using TicketMiser.Core.Contracts;
using TicketMiser.Ingestion.Adapters;

namespace TicketMiser.Tests.Adapters;

/// <summary>
/// Parsing is pinned against recorded payloads rather than a live call, so the suite stays
/// deterministic, offline and free. Until a key exists the fixtures are documentation samples
/// (see the .meta.json beside each); the assertions are written to the documented field names
/// and will hold or fail honestly against the real thing.
/// </summary>
public class TicketmasterParseTests
{
    internal static JsonElement Load(string source, string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", source, fixture);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 18, 15, 5, 0, TimeSpan.Zero);

    [Fact]
    public void An_event_carries_its_on_sale_time_presales_venue_and_all_in_price()
    {
        var parsed = TicketmasterDiscoveryAdapter.ParseEvent(Load("ticketmaster", "event.json"), ObservedAt);

        Assert.NotNull(parsed);
        var (evt, observation) = parsed.Value;

        Assert.Equal("Z7r9jZ1AdF9Ex", evt.SourceEventId);
        Assert.Equal("Example Headliner", evt.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), evt.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero), evt.OnSaleAt);
        Assert.False(evt.OnSaleTbd);
        Assert.Equal("onsale", evt.Status);
        Assert.Equal("concert", evt.CategoryKey);

        Assert.Equal("Bridgestone Arena", evt.Venue.Name);
        Assert.Equal("KovZpZAFnIEA", evt.Venue.SourceVenueId);
        Assert.Equal("Nashville", evt.Venue.City);
        Assert.Equal("TN", evt.Venue.State);
        Assert.Equal("America/Chicago", evt.Venue.Timezone);

        Assert.Equal("Example Headliner", evt.Performer?.Name);
        Assert.Equal("K8vZ9171ex7", evt.Performer?.SourcePerformerId);

        Assert.Collection(evt.PresaleWindows,
            p => Assert.Equal("Artist Presale", p.Name),
            p => Assert.Equal("Live Nation Presale", p.Name));

        Assert.NotNull(observation);
        Assert.Equal(59.5m, observation!.Lowest);
        Assert.Equal(249.5m, observation.Highest);
        Assert.Equal("USD", observation.Currency);
        Assert.True(observation.AllIn);
        Assert.Equal(ObservedAt, observation.ObservedAt);
    }

    [Fact]
    public void A_search_page_drops_rows_with_no_venue_and_keeps_a_time_tba_row_with_its_local_date()
    {
        var (events, observations) = TicketmasterDiscoveryAdapter.ParseEvents(Load("ticketmaster", "search.json"), ObservedAt);

        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.SourceEventId == "Z7r9jZ1AdBAD");

        var tba = events.Single(e => e.SourceEventId == "Z7r9jZ1AdTBA");
        Assert.True(tba.OnSaleTbd);
        Assert.Null(tba.OnSaleAt);
        Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(tba.StartsAt.UtcDateTime));

        // Only the row with a price range yields an observation, and its all-in flag is false.
        var observation = Assert.Single(observations);
        Assert.False(observation.AllIn);
    }

    [Fact]
    public void All_inclusive_pricing_is_false_when_absent_and_read_from_either_shape()
    {
        Assert.False(TicketmasterDiscoveryAdapter.ReadAllInclusive(JsonDocument.Parse("{}").RootElement));
        Assert.True(TicketmasterDiscoveryAdapter.ReadAllInclusive(JsonDocument.Parse("""{"allInclusivePricing": true}""").RootElement));
        Assert.True(TicketmasterDiscoveryAdapter.ReadAllInclusive(JsonDocument.Parse("""{"allInclusivePricing": {"enabled": true}}""").RootElement));
        Assert.False(TicketmasterDiscoveryAdapter.ReadAllInclusive(JsonDocument.Parse("""{"allInclusivePricing": "yes"}""").RootElement));
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
}
