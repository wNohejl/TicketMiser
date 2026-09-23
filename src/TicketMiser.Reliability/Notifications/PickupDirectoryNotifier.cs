using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// The default notifier: sends nothing anywhere. Each message is written as an RFC 5322 .eml
/// file into <see cref="NotificationOptions.PickupDirectory"/>, which any mail client opens,
/// so a developer can read exactly what would have gone out — headers included — without an
/// account at a provider and without a real address ever receiving a test.
/// </summary>
public sealed class PickupDirectoryNotifier(
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<PickupDirectoryNotifier> logger,
    IHostEnvironment? environment = null) : INotifier
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>The resolved directory: absolute as configured, or under the content root.</summary>
    public string Directory => Path.IsPathRooted(_options.PickupDirectory)
        ? _options.PickupDirectory
        : Path.GetFullPath(Path.Combine(environment?.ContentRootPath ?? AppContext.BaseDirectory, _options.PickupDirectory));

    public async Task<string?> SendAsync(MailMessage message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var messageId = $"<{id}@ticketmiser.pickup>";

        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory,
            $"{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'.'fff", CultureInfo.InvariantCulture)}-{id[..8]}.eml");

        await File.WriteAllTextAsync(path, Write(message, _options.From, messageId, now), new UTF8Encoding(false), ct);

        logger.LogInformation("Email to the pickup directory, not sent: {Path}", path);
        return messageId;
    }

    /// <summary>The message as RFC 5322 text: CRLF line endings, UTF-8 bodies sent 8bit, encoded-word subjects.</summary>
    public static string Write(MailMessage message, string from, string messageId, DateTimeOffset date)
    {
        var sb = new StringBuilder();

        void Header(string name, string value) => sb.Append(name).Append(": ").Append(Clean(value)).Append("\r\n");

        Header("Date", date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
        Header("From", from);
        Header("To", message.To);
        Header("Subject", EncodeWord(message.Subject));
        Header("Message-ID", messageId);
        Header("MIME-Version", "1.0");

        foreach (var (name, value) in message.Headers)
            Header(name, value);

        if (message.HtmlBody is null)
        {
            Header("Content-Type", "text/plain; charset=utf-8");
            Header("Content-Transfer-Encoding", "8bit");
            sb.Append("\r\n").Append(Crlf(message.TextBody));
            return sb.ToString();
        }

        var boundary = $"=_tm_{Guid.NewGuid():N}";
        Header("Content-Type", $"multipart/alternative; boundary=\"{boundary}\"");
        sb.Append("\r\n");

        foreach (var (type, body) in new[] { ("text/plain", message.TextBody), ("text/html", message.HtmlBody) })
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: 8bit\r\n\r\n");
            sb.Append(Crlf(body)).Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    /// <summary>A header value on one line: a CR or LF in a value is how a header is injected.</summary>
    private static string Clean(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>RFC 2047 encoded-word when the text is not plain ASCII; unchanged otherwise.</summary>
    private static string EncodeWord(string text)
        => text.All(c => c is >= ' ' and <= '~')
            ? text
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";

    private static string Crlf(string text)
    {
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        return body.EndsWith("\r\n", StringComparison.Ordinal) ? body : body + "\r\n";
    }
}
