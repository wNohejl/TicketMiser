namespace TicketMiser.Core.Entities;

/// <summary>
/// One address asking to be told about one event, and nothing else.
///
/// <para>
/// Personal data, held under rule 8 of docs/legal-guidelines.md: the address is used only
/// for the alert it was given for, every email carries a one-click unsubscribe, and neither
/// this table nor <see cref="AlertDelivery"/> is ever in a snapshot that leaves the machine
/// (scripts/publish-data.ps1 excludes both tables' rows).
/// </para>
///
/// <para>
/// Double opt-in: a row is created unconfirmed, a confirmation email carries
/// <see cref="ConfirmToken"/>, and nothing but that confirmation is ever sent to an address
/// until <see cref="ConfirmedAt"/> is set. So a stranger typing someone else's address costs
/// that person one email, at most once an hour.
/// </para>
/// </summary>
public class Subscription
{
    public const int EmailMaxLength = 254;
    public const int TokenMaxLength = 64;

    public int Id { get; set; }

    /// <summary>Trimmed and lower-cased on the way in, so one person is one row per event.</summary>
    public string Email { get; set; } = string.Empty;

    public int EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>
    /// The account whose address this is, once that address has signed in. Set when a sign-in
    /// claims the address's confirmed subscriptions as owned watches, and when an account's
    /// watch stands in for a subscription; null for an address that never signed in.
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The secret in the confirmation link. Random, url-safe, never derived from the address.</summary>
    public string ConfirmToken { get; set; } = string.Empty;

    /// <summary>When the last confirmation email went out; the hourly throttle reads it. Null until one has.</summary>
    public DateTimeOffset? ConfirmationSentAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The secret in every unsubscribe link and List-Unsubscribe header. Distinct from <see cref="ConfirmToken"/>.</summary>
    public string UnsubscribeToken { get; set; } = string.Empty;

    public DateTimeOffset? UnsubscribedAt { get; set; }

    /// <summary>Confirmed and not unsubscribed: the only state an alert is ever sent to.</summary>
    public bool IsActive => ConfirmedAt is not null && UnsubscribedAt is null;
}

/// <summary>
/// One alert sent to one subscriber. The unique index on (AlertId, SubscriptionId) is the
/// idempotency guarantee: a second evaluation, or a second process, cannot record the same
/// delivery twice, and the delivery service reads the absence of this row as "not yet sent".
/// </summary>
public class AlertDelivery
{
    public long Id { get; set; }

    public long AlertId { get; set; }
    public Alert? Alert { get; set; }

    public int SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }

    public DateTimeOffset SentAt { get; set; }

    /// <summary>The provider's id for the message, for tracing a bounce. Null from a provider that gives none.</summary>
    public string? ProviderMessageId { get; set; }
}
