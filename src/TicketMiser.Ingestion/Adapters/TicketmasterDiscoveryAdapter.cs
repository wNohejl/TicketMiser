using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Adapters;

/// <summary>
/// Ticketmaster Discovery API v2: the primary market for every Live Nation room in Nashville,
/// and the only source that states when a sale opens.
///
/// <para>
/// 5,000 calls a day, 5 a second, and deep paging stops at the thousandth item, so a
/// discovery sweep is sliced by date rather than paged to the end. Price ranges refresh at
/// most hourly on their side; polling faster buys status flips only.
/// </para>
///
/// <para>
/// <c>allInclusivePricing</c> is the adapter's statement about its own numbers. When the event
/// says true the range includes the fees programmed on it; when false it is face value and
/// checkout is 24 to 44 percent higher. It is carried on every observation and never inferred.
/// </para>
/// </summary>
public class TicketmasterDiscoveryAdapter(
    HttpClient http,
    IOptions<IngestionOptions> options,
    ILogger<TicketmasterDiscoveryAdapter> logger) : IPriceSource
{
    public const string SourceKey = "ticketmaster";

    /// <summary>
    /// Ticketmaster's own resale marketplace, which the Discovery API returns as separate
    /// events for rooms it does not ticket. Recorded under its own source so its numbers are
    /// resale numbers.
    /// </summary>
    public const string MarketplaceSourceKey = "ticketmaster-resale";

    /// <summary>The API's page ceiling: size times page must stay under this.</summary>
    public const int DeepPagingLimit = 1000;

    public const int PageSize = 200;

    /// <summary>
    /// The on-sale time Ticketmaster returns for a marketplace listing that never had a public
    /// sale of its own. A real date in 1900 is not a date; it is the absence of one.
    /// </summary>
    public static readonly DateTimeOffset PlaceholderOnSale = new(1900, 1, 1, 6, 0, 0, TimeSpan.Zero);

    private readonly IngestionOptions _options = options.Value;

    public string Key => SourceKey;
    public SourceKind Kind => SourceKind.Primary;

    public string KeyFor(ListingChannel channel)
        => channel == ListingChannel.Marketplace ? MarketplaceSourceKey : SourceKey;

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryQuery query, CancellationToken ct)
    {
        var key = RequireKey();
        var events = new List<CanonicalEvent>();
        var requests = 0;

        // Slice the window into chunks the paging cap can hold. Nashville music over four
        // months is a few hundred events; a month at a time stays well under the thousandth item.
        var from = query.From;

        while (from < query.To)
        {
            var to = from.AddDays(30) < query.To ? from.AddDays(30) : query.To;

            for (var page = 0; (page + 1) * PageSize <= DeepPagingLimit; page++)
            {
                ct.ThrowIfCancellationRequested();

                var url = "events.json"
                          + $"?apikey={Uri.EscapeDataString(key)}"
                          + $"&city={Uri.EscapeDataString(query.City)}"
                          + $"&stateCode={Uri.EscapeDataString(query.StateCode)}"
                          + $"&countryCode={Uri.EscapeDataString(query.CountryCode)}"
                          + $"&classificationName={Uri.EscapeDataString(MapCategory(query.CategoryKey))}"
                          + $"&startDateTime={Stamp(from)}"
                          + $"&endDateTime={Stamp(to)}"
                          + "&sort=date,asc"
                          + $"&size={PageSize}&page={page}";

                using var response = await http.GetAsync(url, ct);
                requests++;
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var (pageEvents, _) = ParseEvents(doc.RootElement, DateTimeOffset.UtcNow);
                events.AddRange(pageEvents);

                var totalPages = doc.RootElement.TryGetProperty("page", out var p)
                                 && p.TryGetProperty("totalPages", out var tp)
                                 && tp.TryGetInt32(out var n)
                    ? n
                    : 1;

                if (page + 1 >= totalPages)
                    break;
            }

            from = to;
        }

        logger.LogInformation("{Source}: discovery found {Count} events in {Requests} requests",
            SourceKey, events.Count, requests);

        return new DiscoveryResult(events, new FetchCost(requests));
    }

    public async Task<PriceFetchResult> FetchPricesAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
    {
        var key = RequireKey();
        var found = new List<CanonicalEvent>();
        var observations = new List<CanonicalPriceObservation>();
        var requests = 0;

        foreach (var reference in events)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await http.GetAsync(
                $"events/{Uri.EscapeDataString(reference.SourceEventId)}.json?apikey={Uri.EscapeDataString(key)}", ct);
            requests++;

            // A 404 is an event Ticketmaster no longer lists, which is information rather than
            // a failure: the resolver marks it from the status the next discovery reports.
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

    /// <summary>Reads a search response: every event under <c>_embedded.events</c>.</summary>
    public static (IReadOnlyList<CanonicalEvent> Events, IReadOnlyList<CanonicalPriceObservation> Observations) ParseEvents(
        JsonElement root, DateTimeOffset observedAt)
    {
        var events = new List<CanonicalEvent>();
        var observations = new List<CanonicalPriceObservation>();

        if (!root.TryGetProperty("_embedded", out var embedded)
            || !embedded.TryGetProperty("events", out var list)
            || list.ValueKind != JsonValueKind.Array)
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

    /// <summary>
    /// Reads one event object. Null when it has no id, no venue or no readable start: an event
    /// the resolver cannot place is not stamped with a made-up time.
    /// </summary>
    public static (CanonicalEvent Event, CanonicalPriceObservation? Observation)? ParseEvent(
        JsonElement e, DateTimeOffset observedAt)
    {
        if (!e.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id)
            return null;

        var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;

        if (!e.TryGetProperty("dates", out var dates))
            return null;

        DateTimeOffset? startsAt = null;
        if (dates.TryGetProperty("start", out var start))
        {
            if (start.TryGetProperty("dateTime", out var dt) && dt.TryGetDateTimeOffset(out var utc))
                startsAt = utc;
            else if (start.TryGetProperty("localDate", out var ld) && ld.GetString() is { } localDate
                     && DateOnly.TryParse(localDate, CultureInfo.InvariantCulture, out var day))
            {
                // A local date with no time is a placeholder; the resolver's drift window
                // absorbs it when the real time arrives.
                startsAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(19, 0)), TimeSpan.Zero);
            }
        }

        if (startsAt is null)
            return null;

        var status = dates.TryGetProperty("status", out var st) && st.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;

        CanonicalVenueRef? venue = null;
        CanonicalPerformerRef? performer = null;

        if (e.TryGetProperty("_embedded", out var embedded))
        {
            if (embedded.TryGetProperty("venues", out var venues) && venues.ValueKind == JsonValueKind.Array)
            {
                var v = venues.EnumerateArray().FirstOrDefault();
                if (v.ValueKind == JsonValueKind.Object)
                {
                    venue = new CanonicalVenueRef(
                        Name: Str(v, "name") ?? "Unknown venue",
                        SourceVenueId: Str(v, "id"),
                        City: v.TryGetProperty("city", out var c) ? Str(c, "name") : null,
                        State: v.TryGetProperty("state", out var s) ? Str(s, "stateCode") : null,
                        CountryCode: v.TryGetProperty("country", out var co) ? Str(co, "countryCode") : null,
                        Timezone: Str(v, "timezone"));
                }
            }

            if (embedded.TryGetProperty("attractions", out var attractions) && attractions.ValueKind == JsonValueKind.Array)
            {
                var a = attractions.EnumerateArray().FirstOrDefault();
                if (a.ValueKind == JsonValueKind.Object && Str(a, "name") is { } performerName)
                    performer = new CanonicalPerformerRef(performerName, Str(a, "id"));
            }
        }

        if (venue is null)
            return null;

        DateTimeOffset? onSaleAt = null;
        var onSaleTbd = false;
        var presales = new List<CanonicalPresale>();

        if (e.TryGetProperty("sales", out var sales))
        {
            if (sales.TryGetProperty("public", out var pub))
            {
                onSaleAt = Timestamp(pub, "startDateTime");
                if (onSaleAt is { } stamp && stamp.Year < 1970)
                    onSaleAt = null;

                onSaleTbd = pub.TryGetProperty("startTBD", out var tbd) && tbd.ValueKind == JsonValueKind.True;
            }

            if (sales.TryGetProperty("presales", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var presale in list.EnumerateArray())
                {
                    presales.Add(new CanonicalPresale(
                        Str(presale, "name") ?? "Presale",
                        Timestamp(presale, "startDateTime"),
                        Timestamp(presale, "endDateTime")));
                }
            }
        }

        string? category = null;
        if (e.TryGetProperty("classifications", out var classifications) && classifications.ValueKind == JsonValueKind.Array)
        {
            var first = classifications.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("segment", out var segment))
                category = MapSegment(Str(segment, "name"));
        }

        var canonical = new CanonicalEvent(
            SourceEventId: id,
            Name: name,
            Venue: venue,
            StartsAt: startsAt.Value,
            Performer: performer,
            CategoryKey: category,
            Status: status,
            OnSaleAt: onSaleAt,
            OnSaleTbd: onSaleTbd,
            Presales: presales,
            Channel: ReadChannel(e));

        CanonicalPriceObservation? observation = null;

        if (e.TryGetProperty("priceRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
        {
            // The "standard" range is the one to compare; other types are packages and parking.
            var standard = ranges.EnumerateArray()
                .FirstOrDefault(r => string.Equals(Str(r, "type"), "standard", StringComparison.OrdinalIgnoreCase));

            if (standard.ValueKind != JsonValueKind.Object)
                standard = ranges.EnumerateArray().FirstOrDefault();

            if (standard.ValueKind == JsonValueKind.Object)
            {
                observation = new CanonicalPriceObservation(
                    SourceEventId: id,
                    ObservedAt: observedAt,
                    Currency: Str(standard, "currency") ?? "USD",
                    Lowest: Dec(standard, "min"),
                    Average: null,
                    Highest: Dec(standard, "max"),
                    ListingCount: null,
                    AllIn: ReadAllInclusive(e),
                    EventStatusCode: status);
            }
        }

        return (canonical, observation);
    }

    /// <summary>
    /// The all-in flag, whichever shape it arrives in. The real payload carries it as
    /// <c>ticketing.allInclusivePricing.enabled</c>; the documentation shows it at the top
    /// level. Absent means false: an adapter that cannot say must say false, never true.
    /// </summary>
    public static bool ReadAllInclusive(JsonElement e)
    {
        if (e.TryGetProperty("ticketing", out var ticketing)
            && ticketing.ValueKind == JsonValueKind.Object
            && ticketing.TryGetProperty("allInclusivePricing", out var nested))
            return Enabled(nested);

        return e.TryGetProperty("allInclusivePricing", out var flag) && Enabled(flag);

        static bool Enabled(JsonElement flag) => flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object => flag.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            _ => false
        };
    }

    /// <summary>
    /// Primary unless the outlets say this listing is Ticketmaster's marketplace for a room
    /// somebody else tickets. A real payload for the Ryman carries a <c>venueBoxOffice</c>
    /// outlet pointing at axs.com beside a <c>tmMarketPlace</c> outlet; the primary listing
    /// for the same show has no outlets at all.
    /// </summary>
    public static ListingChannel ReadChannel(JsonElement e)
    {
        if (!e.TryGetProperty("outlets", out var outlets) || outlets.ValueKind != JsonValueKind.Array)
            return ListingChannel.Primary;

        var marketplace = outlets.EnumerateArray()
            .Any(o => string.Equals(Str(o, "type"), "tmMarketPlace", StringComparison.OrdinalIgnoreCase));

        return marketplace ? ListingChannel.Marketplace : ListingChannel.Primary;
    }

    private static string MapCategory(string categoryKey) => categoryKey switch
    {
        "concert" => "music",
        "theater" => "arts & theatre",
        _ => categoryKey
    };

    private static string? MapSegment(string? segment) => segment?.ToLowerInvariant() switch
    {
        "music" => "concert",
        "sports" => "sports",
        "arts & theatre" => "theater",
        null => null,
        _ => segment.ToLowerInvariant()
    };

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string property)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

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
        => e.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private string RequireKey()
        => string.IsNullOrWhiteSpace(_options.Ticketmaster.ApiKey)
            ? throw new InvalidOperationException($"{SourceKey}: no API key configured.")
            : _options.Ticketmaster.ApiKey;
}
