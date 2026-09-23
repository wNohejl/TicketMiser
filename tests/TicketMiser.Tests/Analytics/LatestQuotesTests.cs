using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// Each source's latest word on one event, as All sources and the destinations read it: the
/// stream's newest row, unless the on-sale record has a newer tick. One quote per source,
/// primary first, never a feed.
/// </summary>
public class LatestQuotesTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };
    private static readonly Source Feed = new() { Id = 4, Key = "ticketmaster-feed", Name = "Discovery Feed", Kind = SourceKind.Feed };

    private static readonly Dictionary<int, Source> Sources = new[] { Ticketmaster, SeatGeek, Feed }.ToDictionary(s => s.Id);

    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_newest_reading_wins_whichever_table_it_is_in()
    {
        var observations = new[]
        {
            new PriceObservation { EventId = 7, SourceId = SeatGeek.Id, ObservedAt = T0, Lowest = 90m, AllIn = true },
            new PriceObservation { EventId = 7, SourceId = SeatGeek.Id, ObservedAt = T0.AddHours(1), Lowest = 84m, AllIn = true },
            new PriceObservation { EventId = 7, SourceId = Ticketmaster.Id, ObservedAt = T0.AddHours(2), Lowest = 59.5m, AllIn = true },
            new PriceObservation { EventId = 7, SourceId = Feed.Id, ObservedAt = T0, Lowest = 1m, AllIn = true },
            new PriceObservation { EventId = 8, SourceId = SeatGeek.Id, ObservedAt = T0.AddHours(5), Lowest = 1m, AllIn = true }
        };

        var ticks = new[]
        {
            // Older than the stream's row: the stream stands.
            new OnSaleTick { EventId = 7, SourceId = SeatGeek.Id, ObservedAt = T0.AddMinutes(30), Lowest = 200m, AllIn = true },
            // Newer than the stream's row: the sale is running and the tick is the truth.
            new OnSaleTick { EventId = 7, SourceId = Ticketmaster.Id, ObservedAt = T0.AddHours(3), Lowest = null, PrimaryStatus = InventoryStatus.NotAvailable }
        };

        var quotes = LatestQuotes.For(7, observations, ticks, Sources);

        Assert.Equal(["Ticketmaster", "SeatGeek"], quotes.Select(q => q.Source.Name));

        var tm = quotes[0];
        Assert.True(tm.FromOnSaleRecord);
        Assert.Null(tm.Lowest);
        Assert.Equal(InventoryStatus.NotAvailable, tm.PrimaryStatus);

        var sg = quotes[1];
        Assert.False(sg.FromOnSaleRecord);
        Assert.Equal(84m, sg.Lowest);
    }
}
