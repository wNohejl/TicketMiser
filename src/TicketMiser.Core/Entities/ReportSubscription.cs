namespace TicketMiser.Core.Entities;

/// <summary>
/// One address asking for the monthly Nashville report by email, and nothing else.
///
/// <para>
/// Its own list, on purpose. Rule 8 of docs/legal-guidelines.md holds an address to the
/// purpose it was given for: an address given for an event's "face value is back" alert
/// (<see cref="Subscription"/>) was given for that alert only and is never mailed a report,
/// and an address on this list is mailed the report and nothing else. Neither this table nor
/// <see cref="ReportDelivery"/> is ever in a snapshot that leaves the machine
/// (scripts/publish-data.ps1 excludes both tables' rows).
/// </para>
///
/// <para>
/// Double opt-in, as <see cref="Subscription"/> is: a row is created unconfirmed, a
/// confirmation email carries <see cref="ConfirmToken"/>, and no report is ever sent to an
/// address until <see cref="ConfirmedAt"/> is set.
/// </para>
/// </summary>
public class ReportSubscription
{
    public int Id { get; set; }

    /// <summary>Normalised as <see cref="Subscription.Email"/> is; one row per address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The account whose address this is, when one exists. Null for an address that never signed in.</summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The secret in the confirmation link. Random, url-safe, never derived from the address.</summary>
    public string ConfirmToken { get; set; } = string.Empty;

    /// <summary>When the last confirmation email went out; the hourly throttle reads it.</summary>
    public DateTimeOffset? ConfirmationSentAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The secret in every unsubscribe link and List-Unsubscribe header. Distinct from <see cref="ConfirmToken"/>.</summary>
    public string UnsubscribeToken { get; set; } = string.Empty;

    public DateTimeOffset? UnsubscribedAt { get; set; }

    /// <summary>Confirmed and not unsubscribed: the only state a report is ever sent to.</summary>
    public bool IsActive => ConfirmedAt is not null && UnsubscribedAt is null;
}

/// <summary>
/// One month's report sent to one subscriber. The unique index on (ReportSubscriptionId,
/// Month) is the idempotency guarantee: a second run of the send cannot mail the same month
/// to the same address twice.
/// </summary>
public class ReportDelivery
{
    public const int MonthLength = 7;

    public long Id { get; set; }

    public int ReportSubscriptionId { get; set; }
    public ReportSubscription? ReportSubscription { get; set; }

    /// <summary><c>yyyy-MM</c>, the report's address under /reports.</summary>
    public string Month { get; set; } = string.Empty;

    public DateTimeOffset SentAt { get; set; }

    /// <summary>The provider's id for the message, for tracing a bounce. Null from a provider that gives none.</summary>
    public string? ProviderMessageId { get; set; }
}
