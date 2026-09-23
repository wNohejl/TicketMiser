namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// One email, provider-neutral. Plain text is required and always sent; HTML is optional.
/// <see cref="Headers"/> carries the extra headers a message needs, such as
/// <c>List-Unsubscribe</c> and <c>List-Unsubscribe-Post</c> (RFC 8058).
/// </summary>
public record MailMessage(
    string To,
    string Subject,
    string TextBody,
    string? HtmlBody,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Sends one email. Implementations throw when the message was not accepted, so a caller never
/// records a delivery that did not happen; they return the provider's message id when it gives
/// one. Exactly one implementation knows any given provider.
/// </summary>
public interface INotifier
{
    Task<string?> SendAsync(MailMessage message, CancellationToken ct);
}
