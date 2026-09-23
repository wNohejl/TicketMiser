using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The sources, events and fake queries the price windows' render tests share. The fakes hand
/// back whatever the test composed with the real Core composition, so a panel is judged on
/// what it says about a history, not on how one was fetched.
/// </summary>
internal static class PriceFixtures
{
    public static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    public static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };
    public static readonly Source StubHub = new() { Id = 3, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale, BaseUrl = "https://api.stubhub.example/" };

    public static readonly Dictionary<int, Source> Sources = new[] { Ticketmaster, SeatGeek, StubHub }.ToDictionary(s => s.Id);

    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static Venue Bridgestone(string provider = "ticketmaster") => new()
    {
        Id = 30,
        Name = "Bridgestone Arena",
        City = "Nashville",
        State = "TN",
        Timezone = "America/Chicago",
        TicketingProvider = provider,
        Capacity = 19_891
    };

    public static Performer Performer() => new() { Id = 20, Name = "Example Performer" };

    public static Event Event(int id = 7, string name = "Example Tour", Venue? venue = null, DateTimeOffset? startsAt = null) => new()
    {
        Id = id,
        Name = name,
        StartsAt = startsAt ?? Now.AddDays(30),
        OnSaleAt = Now.AddDays(-10),
        VenueId = (venue ?? Bridgestone()).Id,
        Venue = venue ?? Bridgestone(),
        PerformerId = 20,
        Performer = Performer(),
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    public static PriceObservation Obs(Source source, int daysAgo, decimal lowest, bool allIn, int eventId = 7) => new()
    {
        EventId = eventId,
        SourceId = source.Id,
        ObservedAt = Now.AddDays(-daysAgo),
        Lowest = lowest,
        AllIn = allIn,
        ListingCount = source.Kind == SourceKind.Resale ? 100 : null
    };

    /// <summary>A history over three weeks: Ticketmaster face value, SeatGeek and StubHub all-in, one purchase at SeatGeek.</summary>
    public static PriceHistory History(Event evt, bool withPurchase = true)
        => PriceHistory.Compose(
            evt,
            [
                Obs(Ticketmaster, 20, 59.5m, allIn: false, eventId: evt.Id),
                Obs(Ticketmaster, 5, 65m, allIn: false, eventId: evt.Id),
                Obs(SeatGeek, 20, 120m, allIn: true, eventId: evt.Id),
                Obs(SeatGeek, 10, 98m, allIn: true, eventId: evt.Id),
                Obs(SeatGeek, 2, 84m, allIn: true, eventId: evt.Id),
                Obs(StubHub, 15, 110m, allIn: true, eventId: evt.Id)
            ],
            [],
            [],
            withPurchase
                ? [new Purchase { EventId = evt.Id, SourceId = SeatGeek.Id, Quantity = 2, PaidPerTicket = 95m, AllIn = true, PurchasedAt = Now.AddDays(-9) }]
                : [],
            Sources);

    public static SourceQuote Quote(Source source, decimal? lowest, bool? allIn, int? listings = null, string? status = null)
        => new(source, lowest, allIn, listings, "USD", Now.AddMinutes(-12), status, false);
}

internal sealed class FakePriceHistoryQueries : IPriceHistoryQueries
{
    public List<Event> Choices { get; } = [];
    public Dictionary<int, PriceHistory> Histories { get; } = [];
    public Dictionary<int, EventQuotes> Quotes { get; } = [];
    public Dictionary<int, PriceTrend> Trends { get; } = [];
    public List<int> Loaded { get; } = [];

    public Task<IReadOnlyList<Event>> ChoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Event>>(Choices);

    public Task<PriceHistory?> LoadAsync(int eventId, CancellationToken ct = default)
    {
        Loaded.Add(eventId);
        return Task.FromResult(Histories.GetValueOrDefault(eventId));
    }

    public Task<PriceTrend?> TrendAsync(int eventId, CancellationToken ct = default)
        => Task.FromResult(Trends.GetValueOrDefault(eventId));

    public Task<EventQuotes?> QuotesAsync(int eventId, CancellationToken ct = default)
        => Task.FromResult(Quotes.GetValueOrDefault(eventId));
}

internal sealed class FakeDestinationQueries : IDestinationQueries
{
    public List<PerformerSummary> Performers { get; } = [];
    public Dictionary<int, PerformerDetail> PerformerDetails { get; } = [];
    public Dictionary<int, VenueDetail> VenueDetails { get; } = [];
    public List<string?> Searches { get; } = [];

    public Task<IReadOnlyList<PerformerSummary>> PerformersAsync(string? search, CancellationToken ct = default)
    {
        Searches.Add(search);

        IReadOnlyList<PerformerSummary> rows = string.IsNullOrWhiteSpace(search)
            ? Performers
            : Performers.Where(p => p.Performer.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        return Task.FromResult(rows);
    }

    public Task<PerformerDetail?> PerformerAsync(int performerId, CancellationToken ct = default)
        => Task.FromResult(PerformerDetails.GetValueOrDefault(performerId));

    public Task<VenueDetail?> VenueAsync(int venueId, CancellationToken ct = default)
        => Task.FromResult(VenueDetails.GetValueOrDefault(venueId));
}
