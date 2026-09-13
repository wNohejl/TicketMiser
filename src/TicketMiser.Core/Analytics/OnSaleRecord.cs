using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// The price the sale opened at: the first primary tick at or after minute zero that carried a
/// <c>Lowest</c>. <paramref name="AllIn"/> travels with it because the number means nothing
/// without it — a face-value $59.50 and an all-in $59.50 are different products.
/// </summary>
public sealed record OnSalePrice(decimal Lowest, bool? AllIn, string Currency, int SourceId, int Minute);

/// <summary>
/// What the first hours of a sale add up to, derived from the ticks and nothing else.
///
/// <para>
/// Every minute here is <see cref="OnSaleTick.MinutesFromOnSale"/>, so the facts read against
/// the announced on-sale time rather than the clock. A null means the record has not seen it:
/// no sellout means primary never went to <see cref="InventoryStatus.NotAvailable"/> in the
/// ticks on hand, not that it never will.
/// </para>
/// </summary>
/// <param name="OnSalePrice">See <see cref="Analytics.OnSalePrice"/>. Null until a primary tick after T carries a price.</param>
/// <param name="FewLeftMinute">The first minute primary reported <see cref="InventoryStatus.FewLeft"/>.</param>
/// <param name="SoldOutMinute">The first minute primary reported <see cref="InventoryStatus.NotAvailable"/>.</param>
/// <param name="ReappearedMinute">The first minute after the sellout that primary reported tickets again.</param>
/// <param name="ResaleListingsAtSellout">
/// Listings on the resale market at the sellout minute: the latest <c>ListingCount</c> each resale
/// source had reported at or before that minute, summed. Null when there was no sellout or no
/// resale source had reported a count by then.
/// </param>
public sealed record OnSaleFacts(
    OnSalePrice? OnSalePrice,
    int? FewLeftMinute,
    int? SoldOutMinute,
    int? ReappearedMinute,
    int? ResaleListingsAtSellout);

/// <summary>
/// One event's on-sale record as the desk reads it: the event, the sources that reported on it,
/// every tick in minute order, and the facts derived from them.
/// </summary>
public sealed record OnSaleRecord(
    Event Event,
    IReadOnlyList<Source> Sources,
    IReadOnlyList<OnSaleTick> Ticks,
    OnSaleFacts Facts)
{
    /// <summary>The window the first screen draws: fifteen minutes before T to two hours after.</summary>
    public const int WindowStart = -15;
    public const int WindowEnd = 120;

    /// <summary>
    /// Reads the facts off a set of ticks. Pure, so the window's numbers can be pinned by a test
    /// with no database behind them, and so the public page and the desk window agree because
    /// they call the same function.
    /// </summary>
    /// <param name="ticks">Any order; sorted here by minute, then observation time.</param>
    /// <param name="sources">Every source the ticks name. A tick whose source is missing is treated as neither market.</param>
    public static OnSaleFacts Derive(IEnumerable<OnSaleTick> ticks, IEnumerable<Source> sources)
    {
        var kinds = sources.ToDictionary(s => s.Id, s => s.Kind);
        var ordered = ticks
            .OrderBy(t => t.MinutesFromOnSale)
            .ThenBy(t => t.ObservedAt)
            .ToList();

        var primary = ordered.Where(t => kinds.GetValueOrDefault(t.SourceId) == SourceKind.Primary).ToList();
        var resale = ordered.Where(t => kinds.GetValueOrDefault(t.SourceId) == SourceKind.Resale).ToList();

        // The price at T: a primary number, never a resale one, however early the resale ask
        // arrived. A tick before T with a price is a presale or a placeholder, not the on-sale.
        var opened = primary.FirstOrDefault(t => t.MinutesFromOnSale >= 0 && t.Lowest is not null);
        var price = opened is null
            ? null
            : new OnSalePrice(opened.Lowest!.Value, opened.AllIn, opened.Currency, opened.SourceId, opened.MinutesFromOnSale);

        var fewLeft = primary.FirstOrDefault(t => t.PrimaryStatus == InventoryStatus.FewLeft)?.MinutesFromOnSale;
        var soldOut = primary.FirstOrDefault(t => t.PrimaryStatus == InventoryStatus.NotAvailable);

        int? reappeared = null;
        int? listings = null;

        if (soldOut is not null)
        {
            reappeared = primary
                .FirstOrDefault(t => t.MinutesFromOnSale > soldOut.MinutesFromOnSale
                    && t.PrimaryStatus is InventoryStatus.Available or InventoryStatus.FewLeft)
                ?.MinutesFromOnSale;

            // What the resale market held at that minute: each resale source's latest count as of
            // the sellout, added up. A source that had not yet reported a count contributes nothing
            // rather than zero, and if none had, the answer is "not known" rather than "none".
            var counts = resale
                .Where(t => t.MinutesFromOnSale <= soldOut.MinutesFromOnSale && t.ListingCount is not null)
                .GroupBy(t => t.SourceId)
                .Select(g => g.Last().ListingCount!.Value)
                .ToList();

            listings = counts.Count == 0 ? null : counts.Sum();
        }

        return new OnSaleFacts(price, fewLeft, soldOut?.MinutesFromOnSale, reappeared, listings);
    }

    /// <summary>
    /// Availability as a height on the primary chart. Three steps, so a sellout reads as a drop
    /// to the floor; null for <see cref="InventoryStatus.Unknown"/> and anything unrecognised,
    /// which the chart holds through rather than draws.
    /// </summary>
    public static double? AvailabilityLevel(string? primaryStatus) => primaryStatus switch
    {
        InventoryStatus.Available => 2,
        InventoryStatus.FewLeft => 1,
        InventoryStatus.NotAvailable => 0,
        _ => null
    };
}
