using TicketMiser.Web.Reports;

namespace TicketMiser.Tests.Reports;

/// <summary>
/// A report is Markdown a person reads and publishes, turned into HTML on a page of ours. It
/// cannot add markup to that page: raw HTML is printed as text, and a link keeps its address
/// only when the address is http, https, mailto or on this site. Published is a flag in the
/// front matter and nothing else.
/// </summary>
public class ReportMarkdownTests
{
    private const string Front = "---\npublished: true\ntitle: Fixture report\n---\n\n";

    [Fact]
    public void Raw_html_in_a_report_is_escaped_not_rendered()
    {
        var markdown = Front
            + "<script>alert('x')</script>\n\n"
            + "<div onclick=\"steal()\">block</div>\n\n"
            + "Inline <img src=x onerror=alert(1)> and <iframe src=\"https://example.com\"></iframe> text.\n";

        var html = ReportMarkdown.Parse("2026-09", markdown).Html;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    public void A_link_with_a_script_address_keeps_its_text_and_loses_its_address(string url)
    {
        var html = ReportMarkdown.Parse("2026-09", Front + $"[click]({url}) and ![pic]({url})\n").Html;

        Assert.DoesNotContain(url, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://seatgeek.com")]
    [InlineData("/e/example-tour-ryman-2026-11-19")]
    [InlineData("mailto:reports@example.com")]
    public void An_ordinary_link_is_kept(string url)
    {
        var html = ReportMarkdown.Parse("2026-09", Front + $"[source]({url})\n").Html;

        Assert.Contains($"href=\"{url}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_s_tables_render_and_the_front_matter_does_not()
    {
        var markdown = Front + "# Heading\n\n| Venue | Median fee gap |\n|---|---|\n| Example Hall | 1% |\n";

        var html = ReportMarkdown.Parse("2026-09", markdown).Html;

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Example Hall</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("published", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("---\npublished: true\n---\n# A\n", true)]
    [InlineData("---\npublished: \"true\"\n---\n# A\n", true)]
    [InlineData("---\npublished: false\n---\n# A\n", false)]
    [InlineData("---\ntitle: A\n---\n# A\n", false)]
    [InlineData("# A\n\npublished: true\n", false)]
    public void Only_the_front_matter_flag_publishes(string markdown, bool published)
        => Assert.Equal(published, ReportMarkdown.Parse("2026-09", markdown).Published);

    [Fact]
    public void The_title_is_the_front_matter_s_then_the_first_heading_then_the_month()
    {
        Assert.Equal("Fixture report", ReportMarkdown.Parse("2026-09", Front + "# Other\n").Title);
        Assert.Equal("First heading", ReportMarkdown.Parse("2026-09", "---\npublished: true\n---\n# First heading\n").Title);
        Assert.Equal("Nashville on-sale report, September 2026", ReportMarkdown.Parse("2026-09", "Body only.\n").Title);
    }

    [Theory]
    [InlineData("2026-09", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-9", false)]
    [InlineData("../secrets", false)]
    [InlineData("2026-09-nashville", false)]
    [InlineData("", false)]
    public void Only_a_year_and_month_names_a_report(string month, bool valid)
        => Assert.Equal(valid, ReportMarkdown.IsMonth(month));

    [Fact]
    public void The_library_lists_published_months_newest_first_and_serves_only_those()
    {
        var dir = Directory.CreateTempSubdirectory("tm-reports-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "2026-10-nashville.md"), "---\npublished: true\ntitle: October\n---\n# October\n");
            File.WriteAllText(Path.Combine(dir.FullName, "2026-11-nashville.md"), "---\npublished: true\ntitle: November\n---\n# November\n");
            File.WriteAllText(Path.Combine(dir.FullName, "2026-12-nashville.md"), "---\npublished: false\n---\n# Draft\n");
            File.WriteAllText(Path.Combine(dir.FullName, "notes-nashville.md"), "---\npublished: true\n---\n# Not a month\n");

            var library = new FileReportLibrary(dir.FullName);

            Assert.Equal(["2026-11", "2026-10"], library.Published().Select(r => r.Month));
            Assert.Equal("October", library.Get("2026-10")!.Title);
            Assert.Null(library.Get("2026-12"));
            Assert.Null(library.Get("2027-01"));
            Assert.Null(library.Get("../2026-10"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_library_with_no_folder_has_no_reports()
        => Assert.Empty(new FileReportLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Published());
}
