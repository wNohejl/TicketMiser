using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Reports;

namespace TicketMiser.Tests.Reports;

/// <summary>
/// <c>send-report &lt;yyyy-mm&gt;</c> on the web host's command line: anything else starts the
/// host as usual, and a missing or malformed month is refused before anything is read or sent.
/// The report's summary paragraph, which the email carries, is read from the file.
/// </summary>
public class SendReportCommandTests
{
    [Theory]
    [InlineData]
    [InlineData("--urls=http://localhost:5270")]
    [InlineData("send-reports", "2026-10")]
    [InlineData("SEND-REPORT", "2026-10")]
    public void Other_arguments_are_not_the_command(params string[] args)
        => Assert.Null(SendReportCommand.Parse(args));

    [Theory]
    [InlineData("2026-10")]
    [InlineData("2027-01")]
    public void A_month_is_accepted(string month)
    {
        var parsed = SendReportCommand.Parse(["send-report", month, "--Reports:Path=C:/scratch"]);

        Assert.NotNull(parsed);
        Assert.Equal(month, parsed.Month);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("2026-00")]
    [InlineData("26-10")]
    [InlineData("2026/10")]
    [InlineData("october")]
    [InlineData("../2026-10")]
    [InlineData("--Reports:Path=x")]
    public void A_malformed_month_is_refused(string month)
    {
        var parsed = SendReportCommand.Parse(["send-report", month]);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Month);
        Assert.Contains("send-report <yyyy-mm>", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_month_is_refused()
        => Assert.Null(Assert.IsType<SendReportCommand.Parsed>(SendReportCommand.Parse(["send-report"])).Month);

    [Fact]
    public async Task A_refused_command_exits_2_without_touching_a_service()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var output = new StringWriter();

        var code = await SendReportCommand.RunAsync(services, SendReportCommand.Parse(["send-report", "2026-13"])!, output);

        Assert.Equal(2, code);
        Assert.Contains("'2026-13' is not a month", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_is_the_first_paragraph_as_plain_text()
    {
        const string markdown = "---\npublished: true\ntitle: T\n---\n# T\n\nPrimary lasted **4 minutes** at\n[Ryman](https://example.com) on average.\n\nSecond paragraph.\n";

        Assert.Equal("Primary lasted 4 minutes at Ryman on average.", ReportMarkdown.Parse("2026-10", markdown).Summary);
    }

    [Fact]
    public void The_report_list_uses_the_same_unsubscribe_headers_as_the_alerts()
    {
        var headers = SubscriptionService.UnsubscribeHeaders(new NotificationOptions { PublicBaseUrl = "https://tix.example" }, "/r/unsubscribe/abc");

        Assert.Equal("<https://tix.example/r/unsubscribe/abc>", headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
    }
}
