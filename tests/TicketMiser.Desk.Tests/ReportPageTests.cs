using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Reports;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The monthly reports at /reports: the published ones newest first, an empty list that says
/// when the first report comes, one month's report rendered from its Markdown, and a month
/// that is not published drawn as a 404 that names no report. The reports here are fixtures
/// written in the test, not measurements.
/// </summary>
public class ReportPageTests : DeskTestContext
{
    private sealed class FakeReports(params (string Month, string Markdown)[] files) : IReportLibrary
    {
        private readonly Dictionary<string, ParsedReport> _parsed = files.ToDictionary(f => f.Month, f => ReportMarkdown.Parse(f.Month, f.Markdown));

        public IReadOnlyList<ReportSummary> Published()
            => _parsed.Where(p => p.Value.Published)
                .OrderByDescending(p => p.Key, StringComparer.Ordinal)
                .Select(p => new ReportSummary(p.Key, p.Value.Title))
                .ToList();

        public ReportDocument? Get(string month)
            => _parsed.TryGetValue(month, out var r) && r.Published ? new ReportDocument(month, r.Title, r.Html) : null;
    }

    private const string October = """
        ---
        published: true
        title: Fixture report for October
        ---
        # Fixture report for October

        A fixture, not a measurement.

        ## Minutes to primary sellout

        | Event | Venue | Minutes |
        |---|---|---|
        | Example Tour | Example Hall | 1 |

        <script>alert('x')</script>
        """;

    private const string November = """
        ---
        published: true
        title: Fixture report for November
        ---
        Body.
        """;

    private const string December = """
        ---
        published: false
        title: Draft for December
        ---
        Not yet read.
        """;

    private IRenderedComponent<ReportPage> Open(string? month, params (string, string)[] files)
        => Open(month, null, files);

    private IRenderedComponent<ReportPage> Open(string? month, string? subscribed, params (string, string)[] files)
    {
        Services.AddSingleton<IReportLibrary>(new FakeReports(files));
        return Render<ReportPage>(p => p.Add(x => x.Month, month).Add(x => x.Subscribed, subscribed));
    }

    [Fact]
    public void The_list_shows_published_reports_newest_first_and_no_draft()
    {
        var cut = Open(null, ("2026-10", October), ("2026-11", November), ("2026-12", December));

        var links = cut.FindAll(".reports a");
        Assert.Equal(["/reports/2026-11", "/reports/2026-10"], links.Select(a => a.GetAttribute("href")));
        Assert.Equal(["Fixture report for November", "Fixture report for October"], links.Select(a => a.TextContent));
        Assert.Equal(["November 2026", "October 2026"], cut.FindAll(".reports li span").Select(s => s.TextContent));
        Assert.DoesNotContain("December", cut.Find("main").TextContent);
    }

    [Fact]
    public void With_nothing_published_the_list_says_the_first_report_follows_a_full_month_of_records()
    {
        var cut = Open(null, ("2026-12", December));

        Assert.Empty(cut.FindAll(".reports"));
        var text = string.Join(' ', cut.Find("main").TextContent.Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("No report has been published yet. The first report follows the first full month of on-sale records.", text);
    }

    [Fact]
    public void A_published_month_is_its_report_rendered_with_its_tables_and_no_markup_of_its_own()
    {
        var cut = Open("2026-10", ("2026-10", October));

        Assert.Equal("Fixture report for October", cut.Find("h1").TextContent);
        Assert.Contains("October 2026", cut.Find(".record-page__where").TextContent);

        var body = cut.Find("article.report-body");
        Assert.Equal(["Event", "Venue", "Minutes"], body.QuerySelectorAll("th").Select(th => th.TextContent));
        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("<script>alert('x')</script>", body.TextContent);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/reports");
    }

    [Fact]
    public void An_unpublished_month_is_drawn_as_no_report_and_not_indexed()
    {
        var cut = Open("2026-12", ("2026-12", December));

        Assert.Equal("No report for this month", cut.Find("h1").TextContent);
        Assert.DoesNotContain("Not yet read", cut.Find("main").TextContent);
        Assert.DoesNotContain("Draft for December", cut.Markup);
        Assert.Equal("noindex", cut.Find("meta[name=robots]").GetAttribute("content"));
        Assert.Empty(cut.FindAll("article"));
    }

    [Theory]
    [InlineData("2026-12")]
    [InlineData("2026-09")]
    [InlineData("not-a-month")]
    public void An_unpublished_or_missing_month_is_a_404(string month)
        => Assert.Equal(StatusCodes.Status404NotFound, ReportEndpoints.Month(month, new FakeReports(("2026-12", December))).StatusCode);

    [Fact]
    public void A_published_month_is_a_200()
        => Assert.Equal(StatusCodes.Status200OK, ReportEndpoints.Month("2026-10", new FakeReports(("2026-10", October))).StatusCode);

    [Fact]
    public void The_list_offers_the_report_by_email_posting_to_its_own_list()
    {
        var cut = Open(null, ("2026-10", October));

        var aside = cut.Find("aside#report-subscribe");
        Assert.Equal("Get the monthly Nashville report by email", aside.QuerySelector("h2")!.TextContent);

        var form = aside.QuerySelector("form")!;
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/reports/subscribe", form.GetAttribute("action"));
        Assert.Equal("email", form.QuerySelector("input[name=email]")!.GetAttribute("type"));
        Assert.Null(form.QuerySelector("input[name=month]"));
        Assert.Contains("never for event alerts", aside.TextContent);
        Assert.Contains("one-click unsubscribe", aside.TextContent);
    }

    [Fact]
    public void A_published_report_carries_the_form_with_its_month_so_the_303_returns_to_it()
    {
        var cut = Open("2026-10", ("2026-10", October));

        var form = cut.Find("aside#report-subscribe form");
        Assert.Equal("/reports/subscribe", form.GetAttribute("action"));
        Assert.Equal("2026-10", form.QuerySelector("input[name=month]")!.GetAttribute("value"));
    }

    [Fact]
    public void A_month_with_no_report_offers_no_form()
        => Assert.Empty(Open("2026-12", ("2026-12", December)).FindAll("aside#report-subscribe"));

    [Theory]
    [InlineData("pending", "Check your inbox to confirm.")]
    [InlineData("invalid", "That does not look like an email address.")]
    public void After_the_post_the_page_says_what_happened(string subscribed, string notice)
    {
        var cut = Open(null, subscribed, ("2026-10", October));

        Assert.Contains(notice, cut.Find("aside#report-subscribe .record-page__notice").TextContent);
    }

    [Theory]
    [InlineData("2026-10", ReportSubscribeOutcome.Accepted, "/reports/2026-10?subscribed=pending#report-subscribe")]
    [InlineData(null, ReportSubscribeOutcome.Accepted, "/reports?subscribed=pending#report-subscribe")]
    [InlineData("2026-10", ReportSubscribeOutcome.InvalidAddress, "/reports/2026-10?subscribed=invalid#report-subscribe")]
    [InlineData("//evil.example", ReportSubscribeOutcome.Accepted, "/reports?subscribed=pending#report-subscribe")]
    [InlineData("2026-10/../../x", ReportSubscribeOutcome.Accepted, "/reports?subscribed=pending#report-subscribe")]
    public void The_form_returns_only_to_a_report_page(string? month, ReportSubscribeOutcome outcome, string location)
        => Assert.Equal(location, ReportEndpoints.AfterSubscribe(month, outcome));
}
