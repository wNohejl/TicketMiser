using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// A price source the tests script: it answers with whatever observations were queued and
/// counts what it was asked. No network, no key, no provider shape.
/// </summary>
public sealed class StubPriceSource(string key, SourceKind kind) : IPriceSource
{
    public string Key => key;
    public SourceKind Kind => kind;

    public List<CanonicalEvent> Events { get; } = [];
    public Func<IReadOnlyList<ExternalEventRef>, DateTimeOffset, IReadOnlyList<CanonicalPriceObservation>> Prices { get; set; }
        = (_, _) => [];

    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public int PriceCalls { get; private set; }

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryQuery query, CancellationToken ct)
        => Task.FromResult(new DiscoveryResult(Events, new FetchCost(1)));

    public Task<PriceFetchResult> FetchPricesAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
    {
        PriceCalls++;
        var now = Clock.GetUtcNow();
        var known = Events.Where(e => events.Any(r => r.SourceEventId == e.SourceEventId)).ToList();
        return Task.FromResult(new PriceFetchResult(known, Prices(events, now), new FetchCost(events.Count)));
    }

    public static CanonicalEvent Bridgestone(string id, string name, DateTimeOffset startsAt, DateTimeOffset? onSaleAt = null)
        => new(id, name,
            new CanonicalVenueRef("Bridgestone Arena", "tm-venue-1", "Nashville", "TN", "US", "America/Chicago"),
            startsAt,
            new CanonicalPerformerRef(name, "perf-" + name.ToLowerInvariant().Replace(' ', '-')),
            "concert", "onsale", onSaleAt);
}

public sealed class StubAvailabilitySource(string key) : IAvailabilitySource
{
    public string Key => key;
    public Func<IReadOnlyList<ExternalEventRef>, IReadOnlyList<CanonicalAvailability>> Statuses { get; set; } = _ => [];

    public Task<AvailabilityResult> FetchAvailabilityAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
        => Task.FromResult(new AvailabilityResult(Statuses(events), new FetchCost(1)));
}
