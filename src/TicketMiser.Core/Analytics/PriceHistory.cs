using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>Which table a point on the history was read from.</summary>
public enum PricePointOrigin
{
    /// <summary>The observation stream: written on every move, pruned after the event.</summary>
    Observation,

    /// <summary>The on-sale record: one tick per minute of the sale's first hours, never pruned.</summary>
    OnSaleTick,

    /// <summary>The day-of price promoted when the event started, kept for good.</summary>
    FinalPrice
}

/// <summary>One reading of one source's lowest price.</summary>
public sealed record PricePoint(DateTimeOffset At, decimal Lowest, int? ListingCount, PricePointOrigin Origin);

/// <summary>
/// One source's lowest price over time, on one fee basis. A source that changes basis part way
/// through — face value for a month, then all-in once a fixture proved otherwise — is split
/// into two series rather than drawn as one line, because the step between them would read as
/// a price move when it is a change in what the number means. <see cref="BasisChanged"/> flags
/// both halves so the reader is told why one source has two lines.
/// </summary>
public sealed record PriceSeries(Source Source, bool AllIn, string Currency, IReadOnlyList<PricePoint> Points, bool BasisChanged)
{
    public SourceKind Kind => Source.Kind;

    public PricePoint First => Points[0];

    public PricePoint Latest => Points[^1];

    public decimal Low => Points.Min(p => p.Lowest);

    public decimal High => Points.Max(p => p.Lowest);

    /// <summary>"all-in" or "face value": the word a legend and a cell carry beside the number.</summary>
    public string Basis => AllIn ? "all-in" : "face value";

    /// <summary>The legend's name for the line. Always carries the basis, so no line is read as the other kind.</summary>
    public string Name => $"{Source.Name} · {Basis}";

    /// <summary>The last reading at or before <paramref name="at"/>, or null when the source had not yet reported.</summary>
    public PricePoint? AsOf(DateTimeOffset at)
    {
        PricePoint? held = null;

        foreach (var p in Points)
        {
            if (p.At > at)
                break;

            held = p;
        }

        return held;
    }
}

/// <summary>A logged purchase, placed on the history. Read, never written, by the history.</summary>
/// <param name="Source">Where it was bought; null when bought somewhere the desk does not track, which marks both markets.</param>
public sealed record PurchaseMark(Purchase Purchase, Source? Source)
{
    public SourceKind? Kind => Source?.Kind;

    /// <summary>Whether this purchase belongs on the chart of <paramref name="kind"/>.</summary>
    public bool Marks(SourceKind kind) => Kind is null || Kind == kind;
}

/// <summary>
/// One event's price history, per market and never across them: every priced source's lowest
/// price over time, the event's start, and the purchases logged against it. The two markets
/// are two lists because they are drawn as two charts — a primary face price and a resale ask
/// never share an axis (ADR 0013, legal guidelines rule 5).
/// </summary>
/// <param name="UnstatedReadings">
/// Ticks that carried a price without saying whether fees were included. A number of unknown
/// kind cannot be placed on either basis, so it is counted and left off rather than guessed.
/// </param>
public sealed record PriceHistory(
    Event Event,
    IReadOnlyList<PriceSeries> Primary,
    IReadOnlyList<PriceSeries> Resale,
    IReadOnlyList<PurchaseMark> Purchases,
    int UnstatedReadings)
{
    public bool IsEmpty => Primary.Count == 0 && Resale.Count == 0;

    public IReadOnlyList<PriceSeries> For(SourceKind kind) => kind switch
    {
        SourceKind.Primary => Primary,
        SourceKind.Resale => Resale,
        _ => []
    };

    /// <summary>Every source with a line on the history, once each.</summary>
    public IReadOnlyList<Source> Sources => Primary.Concat(Resale).Select(s => s.Source).DistinctBy(s => s.Id).ToList();

    /// <summary>
    /// Composes the history from the three tables that hold readings. Pure, so the split by
    /// market and basis can be pinned by a test with no database behind it.
    ///
    /// <para>
    /// The observation stream is the history while the event is ahead; the on-sale ticks fill
    /// the sale's first hours at minute resolution and outlive the stream; the final price is
    /// the last point once the event has started. A tick that repeats the previous reading is
    /// dropped, so the tick table's once-a-minute cadence reads as the stream's store-on-change
    /// one; a final price is always kept, because it is the day-of number a purchase is graded
    /// against. Feed sources and sources not in <paramref name="sources"/> are left out.
    /// </para>
    /// </summary>
    public static PriceHistory Compose(
        Event evt,
        IEnumerable<PriceObservation> observations,
        IEnumerable<OnSaleTick> ticks,
        IEnumerable<FinalPrice> finals,
        IEnumerable<Purchase> purchases,
        IReadOnlyDictionary<int, Source> sources)
    {
        var readings = new List<(Source Source, bool AllIn, string Currency, PricePoint Point)>();
        var unstated = 0;

        foreach (var o in observations)
        {
            if (o.EventId == evt.Id && o.Lowest is { } low && Priced(o.SourceId, sources) is { } source)
                readings.Add((source, o.AllIn, o.Currency, new PricePoint(o.ObservedAt, low, o.ListingCount, PricePointOrigin.Observation)));
        }

        foreach (var t in ticks)
        {
            if (t.EventId != evt.Id || t.Lowest is not { } low || Priced(t.SourceId, sources) is not { } source)
                continue;

            if (t.AllIn is not { } allIn)
            {
                unstated++;
                continue;
            }

            readings.Add((source, allIn, t.Currency, new PricePoint(t.ObservedAt, low, t.ListingCount, PricePointOrigin.OnSaleTick)));
        }

        foreach (var f in finals)
        {
            if (f.EventId == evt.Id && f.Lowest is { } low && Priced(f.SourceId, sources) is { } source)
                readings.Add((source, f.AllIn, f.Currency, new PricePoint(f.ObservedAt, low, f.ListingCount, PricePointOrigin.FinalPrice)));
        }

        var groups = readings
            .GroupBy(r => (r.Source.Id, r.AllIn, r.Currency))
            .ToList();

        // A source with more than one group changed basis (or currency) part way through.
        var switched = groups
            .GroupBy(g => g.Key.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var series = groups
            .Select(g =>
            {
                var points = Collapse(g
                    .Select(r => r.Point)
                    .OrderBy(p => p.At)
                    // At one instant the stream outranks the record, and the record the final.
                    .ThenBy(p => p.Origin)
                    .DistinctBy(p => p.At));

                return new PriceSeries(g.First().Source, g.Key.AllIn, g.Key.Currency, points, switched.Contains(g.Key.Id));
            })
            .OrderBy(s => s.Source.Name, StringComparer.Ordinal)
            .ThenByDescending(s => s.AllIn)
            .ToList();

        var marks = purchases
            .Where(p => p.EventId == evt.Id)
            .OrderBy(p => p.PurchasedAt)
            .Select(p => new PurchaseMark(p, p.SourceId is { } id ? sources.GetValueOrDefault(id) : null))
            .ToList();

        return new PriceHistory(
            evt,
            series.Where(s => s.Kind == SourceKind.Primary).ToList(),
            series.Where(s => s.Kind == SourceKind.Resale).ToList(),
            marks,
            unstated);
    }

    private static Source? Priced(int sourceId, IReadOnlyDictionary<int, Source> sources)
        => sources.GetValueOrDefault(sourceId) is { Kind: not SourceKind.Feed } source ? source : null;

    /// <summary>Drops a tick that says what the previous reading said; keeps every observation and every final.</summary>
    private static List<PricePoint> Collapse(IEnumerable<PricePoint> ordered)
    {
        var kept = new List<PricePoint>();

        foreach (var p in ordered)
        {
            if (p.Origin == PricePointOrigin.OnSaleTick && kept.Count > 0 && kept[^1].Lowest == p.Lowest)
                continue;

            kept.Add(p);
        }

        return kept;
    }
}

/// <summary>
/// The shared time axis a market's chart samples its series onto. MudChart's X axis is a list
/// of labels rather than a time scale, so a history of irregular readings has to be put on a
/// regular grid of buckets first: hourly across a short span, daily across a longer one,
/// weekly once daily would crowd the plot.
/// </summary>
/// <param name="Starts">The start of each bucket, in the venue's zone.</param>
public sealed record TimeAxis(IReadOnlyList<DateTimeOffset> Starts, TimeSpan Step)
{
    public const int MaxBuckets = 120;

    public int Count => Starts.Count;

    public DateTimeOffset End(int index) => Starts[index] + Step;

    /// <summary>The bucket holding <paramref name="instant"/>, or -1 when it falls outside the axis.</summary>
    public int IndexOf(DateTimeOffset instant)
    {
        for (var i = 0; i < Starts.Count; i++)
        {
            if (instant >= Starts[i] && instant < End(i))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// An axis from the earliest reading to the latest, with its buckets aligned to the hour or
    /// the day in <paramref name="zone"/> (an IANA id; UTC when it cannot be resolved).
    /// Empty when there are no readings.
    /// </summary>
    public static TimeAxis Over(IEnumerable<PriceSeries> series, string? zone)
    {
        var instants = series.SelectMany(s => s.Points).Select(p => p.At).ToList();
        if (instants.Count == 0)
            return new TimeAxis([], TimeSpan.FromDays(1));

        var first = Local(instants.Min(), zone);
        var last = Local(instants.Max(), zone);
        var span = last - first;

        var step = span <= TimeSpan.FromDays(3) ? TimeSpan.FromHours(1)
            : span <= TimeSpan.FromDays(MaxBuckets) ? TimeSpan.FromDays(1)
            : TimeSpan.FromDays(7);

        var start = step < TimeSpan.FromDays(1)
            ? new DateTimeOffset(first.Year, first.Month, first.Day, first.Hour, 0, 0, first.Offset)
            : new DateTimeOffset(first.Date, first.Offset);

        var starts = new List<DateTimeOffset>();
        for (var at = start; at <= last; at += step)
            starts.Add(at);

        return new TimeAxis(starts, step);
    }

    /// <summary>
    /// One series on the axis, as a step: each bucket carries the last reading at or before the
    /// bucket's end. A bucket before the source's first reading repeats that first reading,
    /// because a line chart cannot draw a gap and a fabricated zero would read as free tickets;
    /// the chart says so in its takeaway.
    /// </summary>
    public double[] Sample(PriceSeries series)
    {
        var data = new double[Starts.Count];
        var held = (double)series.First.Lowest;

        for (var i = 0; i < Starts.Count; i++)
        {
            if (series.AsOf(End(i) - TimeSpan.FromTicks(1)) is { } p)
                held = (double)p.Lowest;

            data[i] = held;
        }

        return data;
    }

    private static DateTimeOffset Local(DateTimeOffset instant, string? zone)
        => zone is { Length: > 0 } ? OnSaleCalendar.InZone(instant, zone) : instant.ToUniversalTime();
}
