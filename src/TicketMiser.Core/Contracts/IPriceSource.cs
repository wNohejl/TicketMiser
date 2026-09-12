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

/// <summary>Canonical event as normalised at the adapter boundary.</summary>
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
    IReadOnlyList<CanonicalPresale>? Presales = null)
{
    public IReadOnlyList<CanonicalPresale> PresaleWindows => Presales ?? [];
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
    FetchCost Cost);

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
