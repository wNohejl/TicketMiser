using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Contracts;

/// <summary>A venue as the provider identifies it. The id makes resolution exact; the name is the fallback.</summary>
public record CanonicalVenueRef(
    string Name,
    string? SourceVenueId = null,
    string? City = null,
    string? State = null,
    string? CountryCode = null,
    string? Timezone = null);

public record CanonicalPerformerRef(string Name, string? SourcePerformerId = null);

/// <summary>One presale window as the source lists it.</summary>
public record CanonicalPresale(string Name, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

/// <summary>
/// Which market a listing is in, as the adapter read it from the payload. A source can carry
/// both: Ticketmaster lists an AXS room's primary sale once, with presales and an on-sale
/// time, and its own resale marketplace for the same show as a second event id whose
/// outlets say <c>tmMarketPlace</c>. The two are different products and are ingested under
/// different source keys.
/// </summary>
public enum ListingChannel
{
    Primary,
    Marketplace
}

/// <summary>Canonical event as normalised at the adapter boundary.</summary>
/// <param name="CrossReferences">
/// Ids for the same event under other sources' keys, where the payload states them.
/// SeatGeek carries the Ticketmaster event id on many events; storing it lets the resolver
/// take the fast path across sources instead of the drift window.
/// </param>
public record CanonicalEvent(
    string SourceEventId,
    string Name,
    CanonicalVenueRef Venue,
    DateTimeOffset StartsAt,
    CanonicalPerformerRef? Performer = null,
    string? CategoryKey = null,
    string? Status = null,
    DateTimeOffset? OnSaleAt = null,
    bool OnSaleTbd = false,
    IReadOnlyList<CanonicalPresale>? Presales = null,
    ListingChannel Channel = ListingChannel.Primary,
    IReadOnlyDictionary<string, string>? CrossReferences = null)
{
    public IReadOnlyList<CanonicalPresale> PresaleWindows => Presales ?? [];

    public IReadOnlyDictionary<string, string> KnownAs => CrossReferences ?? new Dictionary<string, string>();
}

/// <summary>
/// One source's price summary for one event, as normalised at the adapter boundary.
///
/// <paramref name="AllIn"/> is the adapter's statement about its own numbers and is never
/// inferred downstream. An adapter that cannot say must say false.
/// </summary>
public record CanonicalPriceObservation(
    string SourceEventId,
    DateTimeOffset ObservedAt,
    string Currency,
    decimal? Lowest,
    decimal? Average,
    decimal? Highest,
    int? ListingCount,
    bool AllIn,
    decimal? FaceMin = null,
    decimal? FaceMax = null,
    string? EventStatusCode = null);

/// <summary>Inventory status for one event, as the Inventory Status API reports it.</summary>
public record CanonicalAvailability(string SourceEventId, string? PrimaryStatus, string? ResaleStatus);

/// <summary>
/// What a fetch cost us. Providers meter differently, so adapters report both dimensions and
/// the budget guard reconciles against the source's configured ceiling.
/// </summary>
public record FetchCost(int Requests, int Credits = 0);

/// <summary>The window a discovery sweep covers. Geography first, because that is what "Nashville" means.</summary>
public record DiscoveryQuery(
    string City,
    string StateCode,
    string CountryCode,
    string CategoryKey,
    DateTimeOffset From,
    DateTimeOffset To);

public record DiscoveryResult(IReadOnlyList<CanonicalEvent> Events, FetchCost Cost);

public record PriceFetchResult(
    IReadOnlyList<CanonicalEvent> Events,
    IReadOnlyList<CanonicalPriceObservation> Observations,
    FetchCost Cost)
{
    /// <summary>The adapter that produced this, set by the caller so persistence can ask it which key a channel lives under.</summary>
    public IPriceSource Source { get; init; } = null!;
}

public record AvailabilityResult(IReadOnlyList<CanonicalAvailability> Statuses, FetchCost Cost);

/// <summary>An event as one source knows it, for a targeted price fetch.</summary>
public record ExternalEventRef(int EventId, string SourceEventId);

/// <summary>
/// One external ticket source. Implementations own auth, paging, rate-limit awareness and
/// schema normalisation; everything above this interface is provider-agnostic.
/// </summary>
public interface IPriceSource
{
    /// <summary>Matches <see cref="Source.Key"/> in the database.</summary>
    string Key { get; }

    /// <summary>Which market this source sells in. Decides what its numbers may be compared to.</summary>
    SourceKind Kind { get; }

    /// <summary>
    /// The source key an event's rows are recorded under, by channel. A source with one
    /// channel returns <see cref="Key"/> for both; Ticketmaster returns its resale key for a
    /// marketplace listing. Every id, run and observation for that listing then lives under
    /// a source whose kind is resale, and the board never confuses the two.
    /// </summary>
    string KeyFor(ListingChannel channel) => Key;

    /// <summary>Finds events in a place and a window. A feed source answers this from its file.</summary>
    Task<DiscoveryResult> DiscoverAsync(DiscoveryQuery query, CancellationToken ct);

    /// <summary>
    /// Fetches the current summary for named events. A source with no prices (the feed)
    /// returns no observations and a zero cost.
    /// </summary>
    Task<PriceFetchResult> FetchPricesAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct);
}

/// <summary>Implemented by the source that can say whether primary inventory is still available.</summary>
public interface IAvailabilitySource
{
    string Key { get; }

    Task<AvailabilityResult> FetchAvailabilityAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct);
}

/// <summary>
/// Implemented by adapters that can simulate provider faults on demand, so the detect, alert,
/// triage, incident loop can be exercised deliberately. Only development fixtures implement it.
/// </summary>
public interface IFailureInjectable
{
    /// <summary>null = healthy. "error", "timeout" and "empty" simulate the three failure shapes.</summary>
    string? FailureMode { get; set; }
}
