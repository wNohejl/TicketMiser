namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// How email leaves the process. The default sends nothing to the internet: every message is
/// written to <see cref="PickupDirectory"/> as an .eml file a developer can open. Postmark is
/// used only when <see cref="Provider"/> says so <em>and</em> a server token is configured,
/// so a clone with no account can never send by accident.
/// </summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    public const string PickupProvider = "pickup";
    public const string PostmarkProvider = "postmark";

    /// <summary><c>pickup</c> (default) or <c>postmark</c>. Compose maps NOTIFICATIONS_PROVIDER here.</summary>
    public string Provider { get; set; } = PickupProvider;

    /// <summary>The From header. With Postmark this must be a confirmed sender signature on the account.</summary>
    public string From { get; set; } = "TicketMiser <alerts@ticketmiser.invalid>";

    /// <summary>
    /// The site's public origin, e.g. <c>https://tix.example.com</c>, from which every link in an
    /// email is built. Absolute because an email has no page to be relative to; configured
    /// rather than read from a request because the evaluator that sends alerts has no request.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5270";

    /// <summary>Where the pickup notifier writes. Relative paths are under the content root; gitignored.</summary>
    public string PickupDirectory { get; set; } = "data/outbox";

    /// <summary>Postmark server API token. Compose maps POSTMARK_SERVER_TOKEN here; never in a committed file.</summary>
    public string? PostmarkServerToken { get; set; }

    /// <summary>Postmark's transactional stream. Alerts and confirmations are both transactional.</summary>
    public string PostmarkMessageStream { get; set; } = "outbound";

    /// <summary>Minimum gap between two confirmation emails to one subscription.</summary>
    public TimeSpan ConfirmationResendAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>True when the configuration asks for Postmark and gives it a token to do it with.</summary>
    public bool UsesPostmark
        => string.Equals(Provider, PostmarkProvider, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(PostmarkServerToken);

    /// <summary>An absolute link on the public site.</summary>
    public string Link(string path) => $"{PublicBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
