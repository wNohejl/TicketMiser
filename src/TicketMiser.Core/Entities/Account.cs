namespace TicketMiser.Core.Entities;

/// <summary>
/// A fan who signed in. No password, no name: an address that followed a sign-in link, which
/// is all an account is.
///
/// <para>
/// Personal data, held under rule 8 of docs/legal-guidelines.md: the address is used to send
/// sign-in links and the alerts the account asked for, and neither this table nor
/// <see cref="SignInToken"/> is ever in a snapshot that leaves the machine
/// (scripts/publish-data.ps1 leaves both empty and drops the rows they own).
/// </para>
///
/// <para>
/// An account is created when its first sign-in link is followed, not when the form is
/// posted, so an address someone else typed never becomes a row.
/// </para>
/// </summary>
public class Account
{
    public int Id { get; set; }

    /// <summary>Normalised the way <see cref="Subscription.Email"/> is, so an account and its subscriptions agree on the address.</summary>
    public string Email { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}

/// <summary>
/// One sign-in link. Only the SHA-256 of the secret is stored: a copy of the table signs nobody
/// in. Single use, fifteen minutes.
/// </summary>
public class SignInToken
{
    /// <summary>Lowercase hex SHA-256: 64 characters.</summary>
    public const int HashLength = 64;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public long Id { get; set; }

    /// <summary>
    /// The address the link was sent to. An address rather than an account id because the
    /// account does not exist until the link is followed.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the link was followed. Set once; a second use finds it set and signs nobody in.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
