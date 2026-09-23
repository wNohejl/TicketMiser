using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// Sends through Postmark's single-email endpoint, <c>POST https://api.postmarkapp.com/email</c>.
///
/// <para>
/// The only class that knows Postmark. Chosen because it is transactional-only — it does not
/// carry marketing mail, which keeps its sending reputation fit for alerts — and because its
/// API is one JSON POST with a token header, so there is no SDK to take on.
/// </para>
///
/// <para>
/// Registered only when <c>Notifications:Provider</c> is <c>postmark</c> and a server token is
/// configured. The typed client has no retry handler on purpose: Postmark may have accepted a
/// request whose response was lost, and a retried POST is a second email. A failed send throws,
/// no delivery row is written, and the next evaluation tries again.
/// </para>
/// </summary>
public sealed class PostmarkNotifier(
    HttpClient http,
    IOptions<NotificationOptions> options,
    ILogger<PostmarkNotifier> logger) : INotifier
{
    public const string BaseAddress = "https://api.postmarkapp.com/";
    public const string TokenHeader = "X-Postmark-Server-Token";

    private readonly NotificationOptions _options = options.Value;

    /// <summary>Postmark's field names are PascalCase, which is what no naming policy writes.</summary>
    private static readonly JsonSerializerOptions Json = new();

    public async Task<string?> SendAsync(MailMessage message, CancellationToken ct)
    {
        var body = new PostmarkEmail(
            _options.From,
            message.To,
            message.Subject,
            message.TextBody,
            message.HtmlBody,
            message.Headers.Select(h => new PostmarkHeader(h.Key, h.Value)).ToList(),
            _options.PostmarkMessageStream);

        using var request = new HttpRequestMessage(HttpMethod.Post, "email") { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add(TokenHeader, _options.PostmarkServerToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, ct);
        PostmarkResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<PostmarkResponse>(ct);
        }
        catch (JsonException)
        {
            // A gateway error page rather than Postmark's JSON; the status code says enough.
        }

        // Postmark reports a refusal as ErrorCode != 0, with a 422 status; either one is a failure.
        if (!response.IsSuccessStatusCode || result is null || result.ErrorCode != 0)
        {
            throw new HttpRequestException(
                $"Postmark refused the message: HTTP {(int)response.StatusCode}, error {result?.ErrorCode}, {result?.Message}",
                null, response.StatusCode);
        }

        logger.LogInformation("Email accepted by Postmark as {MessageId}", result.MessageId);
        return result.MessageId;
    }

    internal sealed record PostmarkHeader(string Name, string Value);

    internal sealed record PostmarkEmail(
        string From,
        string To,
        string Subject,
        string TextBody,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HtmlBody,
        IReadOnlyList<PostmarkHeader> Headers,
        string MessageStream);

    internal sealed record PostmarkResponse(
        [property: JsonPropertyName("ErrorCode")] int ErrorCode,
        [property: JsonPropertyName("Message")] string? Message,
        [property: JsonPropertyName("MessageID")] string? MessageId);
}
