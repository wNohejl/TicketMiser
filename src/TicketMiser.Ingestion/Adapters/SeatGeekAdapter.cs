using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Adapters;

/// <summary>
/// SeatGeek Platform API: the resale aggregate, and for an AXS or Etix room the only number
/// we can get. Every event carries a <c>stats</c> block with lowest, average, highest and the
/// listing count, which is exactly one observation per source per event.
///
/// <para>
/// It gives no on-sale date, and whether its prices include fees is not documented. Until a
/// saved fixture proves otherwise every observation says <c>AllIn = false</c>, and the cell
/// says so.
/// </para>
/// </summary>
public class SeatGeekAdapter(
    HttpClient http,
    IOptions<IngestionOptions> options,
    ILogger<SeatGeekAdapter> logger) : IPriceSource
{
    public const string SourceKey = "seatgeek";
    public const int PageSize = 100;

    private readonly IngestionOptions _options = options.Value;

    public string Key => SourceKey;
    public SourceKind Kind => SourceKind.Resale;

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryQuery query, CancellationToken ct)
    {
        var events = new List<CanonicalEvent>();
        var requests = 0;

        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();

            var url = "events"
                      + Auth()
                      + $"&venue.city={Uri.EscapeDataString(query.City)}"
                      + $"&venue.state={Uri.EscapeDataString(query.StateCode)}"
                      + $"&taxonomies.name={Uri.EscapeDataString(MapCategory(query.CategoryKey))}"
                      + $"&datetime_utc.gte={Stamp(query.From)}"
                      + $"&datetime_utc.lte={Stamp(query.To)}"
                      + $"&per_page={PageSize}&page={page}";

            using var response = await http.GetAsync(url, ct);
            requests++;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var (pageEvents, _) = ParseEvents(doc.RootElement, DateTimeOffset.UtcNow);
            events.AddRange(pageEvents);

            var total = doc.RootElement.TryGetProperty("meta", out var meta)
                        && meta.TryGetProperty("total", out var t) && t.TryGetInt32(out var n)
                ? n
                : 0;

            if (page * PageSize >= total || pageEvents.Count == 0)
                break;
        }

        logger.LogInformation("{Source}: discovery found {Count} events in {Requests} requests",
            SourceKey, events.Count, requests);

        return new DiscoveryResult(events, new FetchCost(requests));
    }

    public async Task<PriceFetchResult> FetchPricesAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
    {
        var found = new List<CanonicalEvent>();
        var observations = new List<CanonicalPriceObservation>();
        var requests = 0;

        foreach (var reference in events)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await http.GetAsync($"events/{Uri.EscapeDataString(reference.SourceEventId)}{Auth()}", ct);
            requests++;

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                continue;

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var parsed = ParseEvent(doc.RootElement, DateTimeOffset.UtcNow);
            if (parsed is null)
                continue;

            found.Add(parsed.Value.Event);
            if (parsed.Value.Observation is { } observation)
                observations.Add(observation);
        }

        return new PriceFetchResult(found, observations, new FetchCost(requests));
    }

    public static (IReadOnlyList<CanonicalEvent> Events, IReadOnlyList<CanonicalPriceObservation> Observations) ParseEvents(
        JsonElement root, DateTimeOffset observedAt)
    {
        var events = new List<CanonicalEvent>();
        var observations = new List<CanonicalPriceObservation>();

        if (!root.TryGetProperty("events", out var list) || list.ValueKind != JsonValueKind.Array)
            return (events, observations);

        foreach (var element in list.EnumerateArray())
        {
            var parsed = ParseEvent(element, observedAt);
            if (parsed is null)
                continue;

            events.Add(parsed.Value.Event);
            if (parsed.Value.Observation is { } observation)
                observations.Add(observation);
        }

        return (events, observations);
    }

    public static (CanonicalEvent Event, CanonicalPriceObservation? Observation)? ParseEvent(
        JsonElement e, DateTimeOffset observedAt)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;

        var id = e.TryGetProperty("id", out var idEl) ? idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => idEl.GetString(),
            _ => null
        } : null;

        if (id is null)
            return null;

        var startsAt = Timestamp(e, "datetime_utc");
        if (startsAt is null)
            return null;

        if (!e.TryGetProperty("venue", out var v) || v.ValueKind != JsonValueKind.Object || Str(v, "name") is not { } venueName)
            return null;

        var venue = new CanonicalVenueRef(
            Name: venueName,
            SourceVenueId: Num(v, "id"),
            City: Str(v, "city"),
            State: Str(v, "state"),
            CountryCode: Str(v, "country"),
            Timezone: Str(v, "timezone"));

        CanonicalPerformerRef? performer = null;
        if (e.TryGetProperty("performers", out var performers) && performers.ValueKind == JsonValueKind.Array)
        {
            var primary = performers.EnumerateArray()
                .FirstOrDefault(p => p.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True);

            if (primary.ValueKind != JsonValueKind.Object)
                primary = performers.EnumerateArray().FirstOrDefault();

            if (primary.ValueKind == JsonValueKind.Object && Str(primary, "name") is { } performerName)
                performer = new CanonicalPerformerRef(performerName, Num(primary, "id"));
        }

        string? category = null;
        if (e.TryGetProperty("taxonomies", out var taxonomies) && taxonomies.ValueKind == JsonValueKind.Array)
        {
            var first = taxonomies.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                category = Str(first, "name");
        }

        var canonical = new CanonicalEvent(
            SourceEventId: id,
            Name: Str(e, "title") ?? Str(e, "short_title") ?? id,
            Venue: venue,
            StartsAt: startsAt.Value,
            Performer: performer,
            CategoryKey: category,
            Status: Str(e, "status"));

        CanonicalPriceObservation? observation = null;

        if (e.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            var lowest = Dec(stats, "lowest_price");
            var listings = stats.TryGetProperty("listing_count", out var lc) && lc.TryGetInt32(out var count) ? count : (int?)null;

            // An event with no listings has no price, and a null price is what it should say.
            if (lowest is not null || listings is not null)
            {
                observation = new CanonicalPriceObservation(
                    SourceEventId: id,
                    ObservedAt: observedAt,
                    Currency: "USD",
                    Lowest: lowest,
                    Average: Dec(stats, "average_price"),
                    Highest: Dec(stats, "highest_price"),
                    ListingCount: listings,
                    AllIn: false);
            }
        }

        return (canonical, observation);
    }

    private static string MapCategory(string categoryKey) => categoryKey switch
    {
        "theater" => "theater",
        _ => categoryKey
    };

    private string Auth()
    {
        var clientId = _options.SeatGeek.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException($"{SourceKey}: no client id configured.");

        var auth = $"?client_id={Uri.EscapeDataString(clientId)}";
        if (!string.IsNullOrWhiteSpace(_options.SeatGeek.ClientSecret))
            auth += $"&client_secret={Uri.EscapeDataString(_options.SeatGeek.ClientSecret)}";

        return auth;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Num(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : Str(e, property);

    private static decimal? Dec(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static DateTimeOffset? Timestamp(JsonElement e, string property)
        => Str(e, property) is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
