using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// Each source's latest word on one event, from the two tables that hold it: the newest row of
/// the observation stream, or the newest tick of the on-sale record when that is newer or the
/// stream has nothing. The same rule the Watchlist reads by, stated once for the windows that
/// show one event's sources without a watch behind them — All sources, Performer, Venue.
/// </summary>
public static class LatestQuotes
{
    /// <summary>
    /// One quote per priced source. Feed sources and sources missing from
    /// <paramref name="sources"/> are dropped. Rows for other events are ignored, so a caller may
    /// hand over a whole batch and call this once per event.
    /// </summary>
    public static IReadOnlyList<SourceQuote> For(
        int eventId,
        IEnumerable<PriceObservation> observations,
        IEnumerable<OnSaleTick> ticks,
        IReadOnlyDictionary<int, Source> sources)
    {
        var quotes = new Dictionary<int, SourceQuote>();

        foreach (var o in observations.Where(o => o.EventId == eventId).OrderBy(o => o.ObservedAt))
        {
            if (sources.GetValueOrDefault(o.SourceId) is { Kind: not SourceKind.Feed } source)
                quotes[o.SourceId] = new SourceQuote(source, o.Lowest, o.AllIn, o.ListingCount, o.Currency, o.ObservedAt, null, false);
        }

        foreach (var t in ticks.Where(t => t.EventId == eventId).OrderBy(t => t.ObservedAt))
        {
            if (sources.GetValueOrDefault(t.SourceId) is not { Kind: not SourceKind.Feed } source)
                continue;

            // The stream outranks the record for a price, except when the tick is newer: then the
            // sale is running now and the tick is the truth.
            if (!quotes.TryGetValue(t.SourceId, out var existing) || existing.FromOnSaleRecord || t.ObservedAt > existing.ObservedAt)
                quotes[t.SourceId] = new SourceQuote(source, t.Lowest, t.AllIn, t.ListingCount, t.Currency, t.ObservedAt, t.PrimaryStatus, true);
        }

        return quotes.Values
            .OrderBy(q => q.Kind)
            .ThenBy(q => q.Source.Name, StringComparer.Ordinal)
            .ToList();
    }
}
