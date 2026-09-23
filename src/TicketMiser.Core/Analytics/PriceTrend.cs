using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// One market's lowest price, one fee basis, one value per day: the cheapest reading any of the
/// market's sources on that basis held at the end of the day. Built within a market and a basis
/// only — the same comparison <see cref="MarketBest"/> ranks with — so a face-value line and an
/// all-in line in the same market are two lines, never one.
/// </summary>
/// <param name="Days">The calendar days, in the venue's zone, oldest first.</param>
/// <param name="Lowest">Per day; null before any source on this basis had reported.</param>
/// <param name="HeldBy">Per day, the source holding that day's lowest; null where the lowest is.</param>
public sealed record TrendLine(
    SourceKind Kind,
    bool AllIn,
    string Currency,
    IReadOnlyList<DateOnly> Days,
    IReadOnlyList<decimal?> Lowest,
    IReadOnlyList<Source?> HeldBy)
{
    public string Basis => AllIn ? "all-in" : "face value";

    public decimal? Now => Lowest[^1];

    public Source? NowHeldBy => HeldBy[^1];

    /// <summary>The lowest seven days before the last day, when the line reaches back that far.</summary>
    public decimal? WeekAgo => Lowest.Count > 7 ? Lowest[^8] : null;

    /// <summary>Now minus a week ago. Negative means the price has come down.</summary>
    public decimal? WeekChange => Now is { } now && WeekAgo is { } then ? now - then : null;

    /// <summary>The lowest day in the window, and the day it was.</summary>
    public (decimal Lowest, DateOnly Day)? Floor
    {
        get
        {
            (decimal, DateOnly)? floor = null;

            for (var i = 0; i < Lowest.Count; i++)
            {
                if (Lowest[i] is { } v && (floor is null || v < floor.Value.Item1))
                    floor = (v, Days[i]);
            }

            return floor;
        }
    }

    /// <summary>The first day with a value, or -1 when the line never had one.</summary>
    public int FirstReported => Lowest.ToList().FindIndex(v => v is not null);
}

/// <summary>
/// The last few weeks of one event's lowest price, per market, at a glance: the Watchlist's
/// Trend follow-up. Derived from the <see cref="PriceHistory"/> rather than read separately, so
/// the trend and the chart it summarises can never disagree about a day.
/// </summary>
public sealed record PriceTrend(Event Event, DateOnly Through, IReadOnlyList<TrendLine> Primary, IReadOnlyList<TrendLine> Resale)
{
    public const int DefaultDays = 28;

    public bool IsEmpty => Primary.Count == 0 && Resale.Count == 0;

    public IReadOnlyList<TrendLine> For(SourceKind kind) => kind == SourceKind.Primary ? Primary : kind == SourceKind.Resale ? Resale : [];

    /// <summary>
    /// The trend over <paramref name="days"/> days ending at <paramref name="now"/>, or at the
    /// event's start once it has started — a price after the doors open is not a price anyone
    /// could have paid. Days are the venue's calendar days.
    /// </summary>
    public static PriceTrend Compose(PriceHistory history, DateTimeOffset now, int days = DefaultDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);

        var zone = history.Event.Venue?.Timezone;
        var anchor = now < history.Event.StartsAt ? now : history.Event.StartsAt;
        var anchorLocal = Local(anchor, zone);
        var lastDay = DateOnly.FromDateTime(anchorLocal.DateTime);

        var calendar = Enumerable.Range(0, days).Select(i => lastDay.AddDays(i - days + 1)).ToList();

        // The end of each day as an instant; the last day ends at the anchor rather than midnight.
        var cutoffs = calendar
            .Select((d, i) => i == days - 1
                ? anchor
                : new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), anchorLocal.Offset).AddTicks(-1))
            .ToList();

        return new PriceTrend(
            history.Event,
            lastDay,
            Lines(SourceKind.Primary, history.Primary, calendar, cutoffs),
            Lines(SourceKind.Resale, history.Resale, calendar, cutoffs));
    }

    private static List<TrendLine> Lines(SourceKind kind, IReadOnlyList<PriceSeries> series, List<DateOnly> calendar, List<DateTimeOffset> cutoffs)
        => series
            .GroupBy(s => (s.AllIn, s.Currency))
            .OrderByDescending(g => g.Key.AllIn)
            .Select(g =>
            {
                var lowest = new decimal?[calendar.Count];
                var heldBy = new Source?[calendar.Count];

                for (var i = 0; i < calendar.Count; i++)
                {
                    foreach (var s in g)
                    {
                        if (s.AsOf(cutoffs[i]) is { } p && (lowest[i] is null || p.Lowest < lowest[i]))
                        {
                            lowest[i] = p.Lowest;
                            heldBy[i] = s.Source;
                        }
                    }
                }

                return new TrendLine(kind, g.Key.AllIn, g.Key.Currency, calendar, lowest, heldBy);
            })
            .Where(l => l.FirstReported >= 0)
            .ToList();

    private static DateTimeOffset Local(DateTimeOffset instant, string? zone)
        => zone is { Length: > 0 } ? OnSaleCalendar.InZone(instant, zone) : instant.ToUniversalTime();
}
