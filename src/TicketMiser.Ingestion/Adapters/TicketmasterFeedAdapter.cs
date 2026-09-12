using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Adapters;

/// <summary>
/// The Discovery Feed: a daily national file on the same key as the Discovery API, carrying
/// event ids, names, venues, on-sale and presale dates and statuses, and no prices since
/// March 2025. Nothing says it counts against the daily quota, so it is where discovery and
/// on-sale scheduling come from, leaving the quota for prices.
///
/// <para>
/// The file is streamed element by element and filtered to the configured state on the way
/// in; the whole country never sits in memory. The download is refused when the declared
/// length exceeds the configured cap.
/// </para>
/// </summary>
public class TicketmasterFeedAdapter(
    HttpClient http,
    IOptions<IngestionOptions> options,
    ILogger<TicketmasterFeedAdapter> logger) : IPriceSource
{
    public const string SourceKey = "ticketmaster-feed";

    private readonly IngestionOptions _options = options.Value;

    public string Key => SourceKey;
    public SourceKind Kind => SourceKind.Feed;

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryQuery query, CancellationToken ct)
    {
        var key = _options.Ticketmaster.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"{SourceKey}: no Ticketmaster API key configured.");

        var url = $"events.json?apikey={Uri.EscapeDataString(key)}&countryCode={Uri.EscapeDataString(query.CountryCode)}";

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared is { } length && length > _options.DiscoveryFeed.MaxBytes)
            throw new InvalidOperationException(
                $"{SourceKey}: the feed declares {length:N0} bytes, over the {_options.DiscoveryFeed.MaxBytes:N0} cap.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var counted = new ByteCountingStream(stream, _options.DiscoveryFeed.MaxBytes);

        var events = new List<CanonicalEvent>();
        var seen = 0;

        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(counted, cancellationToken: ct))
        {
            seen++;

            var parsed = ParseEvent(element);
            if (parsed is null)
                continue;

            if (!Matches(parsed, query))
                continue;

            events.Add(parsed);
        }

        logger.LogInformation("{Source}: {Kept} of {Seen} feed events are {State} inside the window ({Bytes:N0} bytes read)",
            SourceKey, events.Count, seen, query.StateCode, counted.BytesRead);

        // One request, and the guard sees it against a daily ceiling of a handful.
        return new DiscoveryResult(events, new FetchCost(Requests: 1));
    }

    /// <summary>The feed has no prices. A price fetch is answered with nothing, at no cost.</summary>
    public Task<PriceFetchResult> FetchPricesAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
        => Task.FromResult(new PriceFetchResult([], [], new FetchCost(0)));

    /// <summary>The geographic and category filter, in one place so a test can pin it.</summary>
    public static bool Matches(CanonicalEvent e, DiscoveryQuery query)
        => string.Equals(e.Venue.State, query.StateCode, StringComparison.OrdinalIgnoreCase)
           && string.Equals(e.Venue.CountryCode ?? query.CountryCode, query.CountryCode, StringComparison.OrdinalIgnoreCase)
           && e.StartsAt >= query.From && e.StartsAt <= query.To
           && (e.CategoryKey is null || string.Equals(e.CategoryKey, query.CategoryKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads one feed row. Field names are the feed's documented ones, read leniently; the
    /// first saved fixture is what corrects any of them.
    /// </summary>
    public static CanonicalEvent? ParseEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;

        var id = Str(e, "eventId") ?? Str(e, "id");
        if (id is null)
            return null;

        var startsAt = Timestamp(e, "eventStartDateTime") ?? Timestamp(e, "eventStartDate");
        if (startsAt is null)
            return null;

        var venueName = Str(e, "venueName");
        if (venueName is null)
            return null;

        var venue = new CanonicalVenueRef(
            Name: venueName,
            SourceVenueId: Str(e, "venueId"),
            City: Str(e, "venueCity"),
            State: Str(e, "venueStateCode") ?? Str(e, "venueState"),
            CountryCode: Str(e, "venueCountryCode") ?? Str(e, "venueCountry"),
            Timezone: Str(e, "venueTimezone"));

        CanonicalPerformerRef? performer = null;
        var attraction = Str(e, "attractionName") ?? Str(e, "primaryAttractionName");
        if (attraction is not null)
            performer = new CanonicalPerformerRef(attraction, Str(e, "attractionId") ?? Str(e, "primaryAttractionId"));

        var presales = new List<CanonicalPresale>();
        if (e.TryGetProperty("presales", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
                presales.Add(new CanonicalPresale(
                    Str(p, "presaleName") ?? "Presale",
                    Timestamp(p, "presaleStartDateTime"),
                    Timestamp(p, "presaleEndDateTime")));
        }

        var segment = Str(e, "segment") ?? Str(e, "segmentName") ?? Str(e, "classificationSegment");

        return new CanonicalEvent(
            SourceEventId: id,
            Name: Str(e, "eventName") ?? Str(e, "name") ?? id,
            Venue: venue,
            StartsAt: startsAt.Value,
            Performer: performer,
            CategoryKey: segment?.ToLowerInvariant() switch
            {
                "music" => "concert",
                "sports" => "sports",
                "arts & theatre" => "theater",
                null => null,
                var other => other
            },
            Status: Str(e, "eventStatus"),
            OnSaleAt: Timestamp(e, "onsaleStartDateTime"),
            OnSaleTbd: false,
            Presales: presales);
    }

    private static string? Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Timestamp(JsonElement e, string property)
        => Str(e, property) is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Counts bytes as they pass and refuses to read past the cap, for a response with no declared length.</summary>
    private sealed class ByteCountingStream(Stream inner, long cap) : Stream
    {
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            BytesRead += read;

            if (BytesRead > cap)
                throw new InvalidOperationException($"Feed exceeded the {cap:N0}-byte cap while streaming.");

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
