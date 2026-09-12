using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Lookup over the registered adapters. Everything downstream depends on the interfaces, so
/// adding a provider is a registration change rather than a code change.
/// </summary>
public class SourceRegistry(
    IEnumerable<IPriceSource> priceSources,
    IEnumerable<IAvailabilitySource> availabilitySources)
{
    public IReadOnlyList<IPriceSource> PriceSources { get; } = priceSources.ToList();
    public IReadOnlyList<IAvailabilitySource> AvailabilitySources { get; } = availabilitySources.ToList();

    /// <summary>Sources that quote prices: everything but the feed.</summary>
    public IReadOnlyList<IPriceSource> PricedSources
        => PriceSources.Where(s => s.Kind != SourceKind.Feed).ToList();

    public IReadOnlyList<IPriceSource> DiscoverySources => PriceSources;

    public IPriceSource? Find(string key) => PriceSources.FirstOrDefault(s => s.Key == key);

    public IReadOnlyList<string> RegisteredKeys
        => PriceSources.Select(s => s.Key).Concat(AvailabilitySources.Select(s => s.Key)).Distinct().ToList();
}
