namespace TicketMiser.Core.Entities;

/// <summary>
/// The values <see cref="OnSaleTick.PrimaryStatus"/> takes, as the Inventory Status API spells
/// them. Kept beside the entity rather than in the alert engine because two readers depend on
/// them now: the reappearance alert and the on-sale record's derivation.
/// </summary>
public static class InventoryStatus
{
    public const string Available = "TICKETS_AVAILABLE";
    public const string FewLeft = "FEW_TICKETS_LEFT";
    public const string NotAvailable = "TICKETS_NOT_AVAILABLE";
    public const string Unknown = "UNKNOWN";
}
