namespace TicketMiser.Core.Entities;

/// <summary>
/// One source's price summary for one event at one instant. The unit of observation is the
/// summary, not the listing: SeatGeek will never give listings and Ticketmaster gives a range,
/// so the summary is what every source can honestly supply.
///
/// <para>
/// Append-only and written only on change. A price that has not moved since the last
/// observation is not written, so every row is a real move and the chart is signal rather
/// than sampling noise. Partitioned by month on <see cref="ObservedAt"/>; pruned after the
/// event's <see cref="FinalPrice"/> is promoted.
/// </para>
/// </summary>
public class PriceObservation
{
    public long Id { get; set; }

    public int EventId { get; set; }
    public Event? Event { get; set; }

    public int SourceId { get; set; }
    public Source? Source { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public string Currency { get; set; } = "USD";

    public decimal? Lowest { get; set; }
    public decimal? Average { get; set; }
    public decimal? Highest { get; set; }
    public int? ListingCount { get; set; }

    /// <summary>
    /// Whether <see cref="Lowest"/> and <see cref="Highest"/> include the fees the buyer will
    /// pay. A column, not a convention: the board filters every comparison on it, and a cell
    /// renders it. Ticketmaster says so per event; SeatGeek is treated as false until a
    /// fixture proves otherwise.
    /// </summary>
    public bool AllIn { get; set; }

    /// <summary>Face value, where the source gives it separately. The fee gap is Lowest minus this.</summary>
    public decimal? FaceMin { get; set; }
    public decimal? FaceMax { get; set; }

    public long IngestionRunId { get; set; }
}

/// <summary>
/// One tick of the on-sale record: what every source said about one event at one minute of
/// the sale's first hours. Written at every tick whether or not anything changed, because the
/// point is the timeline, and never pruned, because it is the evidence.
///
/// <para>
/// Partitioned by month on <see cref="ObservedAt"/> like the observation stream, but under a
/// different retention rule: <see cref="PriceObservation"/> is working state until the event,
/// this is the product.
/// </para>
/// </summary>
public class OnSaleTick
{
    public long Id { get; set; }

    public int EventId { get; set; }
    public Event? Event { get; set; }

    public int SourceId { get; set; }
    public Source? Source { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>Minutes from the announced on-sale time; negative before it opened.</summary>
    public int MinutesFromOnSale { get; set; }

    /// <summary>The source's event status code, e.g. "onsale", "offsale".</summary>
    public string? EventStatusCode { get; set; }

    /// <summary>Primary inventory as the Inventory Status API reports it. Null for resale sources.</summary>
    public string? PrimaryStatus { get; set; }

    /// <summary>Resale inventory status where the source reports one.</summary>
    public string? ResaleStatus { get; set; }

    public string Currency { get; set; } = "USD";
    public decimal? Lowest { get; set; }
    public decimal? Highest { get; set; }
    public int? ListingCount { get; set; }
    public bool? AllIn { get; set; }

    public long IngestionRunId { get; set; }
}

/// <summary>
/// The price as it stood when the event started: the one observation per event per source
/// kept permanently. Promoted from the observation stream at event start; the stream is
/// pruned afterwards. This is what "what I paid versus the day-of price" joins to.
/// </summary>
public class FinalPrice
{
    public long Id { get; set; }

    public int EventId { get; set; }
    public Event? Event { get; set; }

    public int SourceId { get; set; }
    public Source? Source { get; set; }

    public string Currency { get; set; } = "USD";
    public decimal? Lowest { get; set; }
    public decimal? Average { get; set; }
    public decimal? Highest { get; set; }
    public int? ListingCount { get; set; }
    public bool AllIn { get; set; }

    /// <summary>When the observation that became the final price was taken.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>When promotion ran. Always at or after the event's start.</summary>
    public DateTimeOffset PromotedAt { get; set; }
}
