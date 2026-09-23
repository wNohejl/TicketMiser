using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Reliability;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Notifications;

/// <summary>
/// The two notifiers, with nothing leaving the machine: the pickup directory writes an .eml a
/// mail client can open, and the Postmark client is judged on the request it builds against a
/// handler that answers in Postmark's shape.
/// </summary>
public class NotifierTests
{
    private static readonly MailMessage Message = new(
        "fan@example.com",
        "Example Tour: Ticketmaster shows tickets available again — Beyoncé",
        "Line one.\nLine two.",
        null,
        new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = "<https://tix.example/s/unsubscribe/abc>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
        });

    private static Microsoft.Extensions.Options.IOptions<NotificationOptions> Options(Action<NotificationOptions>? configure = null)
    {
        var options = new NotificationOptions { From = "TicketMiser <alerts@tix.example>" };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    [Fact]
    public async Task Pickup_writes_one_parseable_eml_with_the_headers_and_the_body()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-outbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 10, 2, 15, 20, 0, TimeSpan.Zero));
            var notifier = new PickupDirectoryNotifier(Options(o => o.PickupDirectory = dir), clock, NullLogger<PickupDirectoryNotifier>.Instance);

            var id = await notifier.SendAsync(Message, CancellationToken.None);

            var file = Assert.Single(Directory.GetFiles(dir, "*.eml"));
            var raw = await File.ReadAllTextAsync(file, Encoding.UTF8);

            // RFC 5322: CRLF everywhere, headers, one blank line, the body.
            Assert.DoesNotContain("\r\r", raw, StringComparison.Ordinal);
            Assert.Equal(raw.Split("\r\n").Length, raw.Split('\n').Length);
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split > 0);

            var headers = raw[..split].Split("\r\n")
                .Select(line => line.Split(": ", 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

            Assert.Equal("fan@example.com", headers["To"]);
            Assert.Equal("TicketMiser <alerts@tix.example>", headers["From"]);
            Assert.Equal("Fri, 02 Oct 2026 15:20:00 +0000", headers["Date"]);
            Assert.Equal(id, headers["Message-ID"]);
            Assert.Equal("<https://tix.example/s/unsubscribe/abc>", headers["List-Unsubscribe"]);
            Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
            Assert.StartsWith("text/plain; charset=utf-8", headers["Content-Type"], StringComparison.Ordinal);

            // A non-ASCII subject is an RFC 2047 encoded-word that decodes to what was sent.
            var subject = headers["Subject"];
            Assert.StartsWith("=?utf-8?B?", subject, StringComparison.Ordinal);
            Assert.Equal(Message.Subject, Encoding.UTF8.GetString(Convert.FromBase64String(subject[10..^2])));

            Assert.Equal("Line one.\r\nLine two.\r\n", raw[(split + 4)..]);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_header_value_cannot_inject_another_header()
    {
        var hostile = Message with { To = "fan@example.com\r\nBcc: everyone@example.com" };

        var raw = PickupDirectoryNotifier.Write(hostile, "a@b.example", "<id@x>", DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("\r\nBcc:", raw, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (PostmarkNotifier Notifier, CapturingHandler Handler) Postmark(HttpStatusCode status, string body)
    {
        var handler = new CapturingHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri(PostmarkNotifier.BaseAddress) };
        var options = Options(o =>
        {
            o.Provider = NotificationOptions.PostmarkProvider;
            o.PostmarkServerToken = "server-token-for-tests";
        });

        return (new PostmarkNotifier(http, options, NullLogger<PostmarkNotifier>.Instance), handler);
    }

    [Fact]
    public async Task Postmark_posts_the_message_as_its_json_with_the_server_token()
    {
        var (notifier, handler) = Postmark(HttpStatusCode.OK,
            """{"To":"fan@example.com","SubmittedAt":"2026-10-02T15:20:00Z","MessageID":"b7bc2f4a-e38e-4336-af7d-e6c392c2f817","ErrorCode":0,"Message":"OK"}""");

        var id = await notifier.SendAsync(Message, CancellationToken.None);

        Assert.Equal("b7bc2f4a-e38e-4336-af7d-e6c392c2f817", id);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.postmarkapp.com/email", handler.Request.RequestUri!.ToString());
        Assert.Equal("server-token-for-tests", Assert.Single(handler.Request.Headers.GetValues(PostmarkNotifier.TokenHeader)));

        using var json = JsonDocument.Parse(handler.RequestBody!);
        var root = json.RootElement;
        Assert.Equal("TicketMiser <alerts@tix.example>", root.GetProperty("From").GetString());
        Assert.Equal("fan@example.com", root.GetProperty("To").GetString());
        Assert.Equal(Message.Subject, root.GetProperty("Subject").GetString());
        Assert.Equal(Message.TextBody, root.GetProperty("TextBody").GetString());
        Assert.False(root.TryGetProperty("HtmlBody", out _));
        Assert.Equal("outbound", root.GetProperty("MessageStream").GetString());

        var headers = root.GetProperty("Headers").EnumerateArray()
            .ToDictionary(h => h.GetProperty("Name").GetString()!, h => h.GetProperty("Value").GetString());
        Assert.Equal("<https://tix.example/s/unsubscribe/abc>", headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public async Task A_postmark_refusal_throws_so_no_delivery_is_recorded()
    {
        var (notifier, _) = Postmark(HttpStatusCode.UnprocessableEntity,
            """{"ErrorCode":300,"Message":"Invalid 'To' address: 'fan'."}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => notifier.SendAsync(Message, CancellationToken.None));

        Assert.Contains("300", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, typeof(PickupDirectoryNotifier))]
    [InlineData("postmark", null, typeof(PickupDirectoryNotifier))]
    [InlineData("postmark", "token", typeof(PostmarkNotifier))]
    public void The_container_holds_the_pickup_directory_unless_postmark_is_configured(string? provider, string? token, Type expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:Provider"] = provider,
            ["Notifications:PostmarkServerToken"] = token
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        using var container = new ServiceCollection()
            .AddLogging()
            .AddTicketMiserNotifications(configuration)
            .BuildServiceProvider();

        Assert.IsType(expected, container.GetRequiredService<INotifier>());
    }

    [Theory]
    [InlineData("postmark", "token", true)]
    [InlineData("Postmark", "token", true)]
    [InlineData("postmark", "", false)]
    [InlineData("postmark", null, false)]
    [InlineData("pickup", "token", false)]
    public void Postmark_is_used_only_when_asked_for_and_given_a_token(string provider, string? token, bool expected)
        => Assert.Equal(expected, new NotificationOptions { Provider = provider, PostmarkServerToken = token }.UsesPostmark);
}
