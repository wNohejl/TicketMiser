namespace TicketMiser.Core.Entities;

/// <summary>
/// What the operator paid, where, when and for how many. The analogue of LineOps's journal
/// entry: the one row a person writes, which the final price later grades.
/// </summary>
public class Purchase
{
    public long Id { get; set; }

    public int EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>The account that logged it, or null for the operator's own. Read only by its owner and the operator.</summary>
    public int? OwnerId { get; set; }
    public Account? Owner { get; set; }

    /// <summary>Where it was bought. Null when bought somewhere we do not track.</summary>
    public int? SourceId { get; set; }
    public Source? Source { get; set; }

    public int Quantity { get; set; } = 1;

    /// <summary>Per ticket, in <see cref="Currency"/>.</summary>
    public decimal PaidPerTicket { get; set; }

    /// <summary>Whether <see cref="PaidPerTicket"/> includes fees. A receipt total does.</summary>
    public bool AllIn { get; set; } = true;

    public string Currency { get; set; } = "USD";

    public DateTimeOffset PurchasedAt { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// Paid versus the day-of price, per ticket, resolved after the event by joining to
    /// <see cref="FinalPrice"/> on the same market. Positive means the operator beat the day-of
    /// price. Null until the event has a final price of the same all-in kind.
    /// </summary>
    public decimal? SavingsVsFinal { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}
