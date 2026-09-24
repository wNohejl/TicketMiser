using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TicketMiser.Web.Reports;

/// <summary>A published report as the list shows it.</summary>
/// <param name="Month"><c>yyyy-mm</c>, the report's address under /reports.</param>
public sealed record ReportSummary(string Month, string Title)
{
    public string MonthName => ReportMarkdown.MonthName(Month);
}

/// <summary>A published report, rendered.</summary>
/// <param name="Html">The report's body as HTML. Raw HTML in the file was escaped, not passed through.</param>
/// <param name="Summary">The report's first paragraph as plain text: the one-paragraph summary the skill writes, and what the report email carries.</param>
public sealed record ReportDocument(string Month, string Title, string Html, string Summary = "")
{
    public string MonthName => ReportMarkdown.MonthName(Month);
}

/// <summary>The monthly reports the site publishes. An interface so a render test can hand the page its reports.</summary>
public interface IReportLibrary
{
    /// <summary>Every published report, newest month first.</summary>
    IReadOnlyList<ReportSummary> Published();

    /// <summary>The report for <c>yyyy-mm</c>, or null when there is none or it is not published.</summary>
    ReportDocument? Get(string month);
}

/// <summary>
/// The reports on disk: <c>{yyyy-mm}-nashville.md</c> files in one folder, which the build
/// copies from <c>docs/reports</c> to <c>reports/</c> beside the assembly (and the Dockerfile
/// into the image). Read on each call; the output cache in front of /reports is what keeps a
/// busy month from reading the disk.
///
/// <para>
/// A file is published only when its front matter says <c>published: true</c>. The
/// monthly-report skill writes the file and stops; a person reads it, sets the flag and
/// commits. Until then the file is in the repository and the folder, and the site 404s it.
/// </para>
/// </summary>
public sealed class FileReportLibrary(string directory) : IReportLibrary
{
    public const string ConfigurationKey = "Reports:Path";

    /// <summary><c>Reports:Path</c> when configured, else <c>reports/</c> beside the assembly.</summary>
    public static FileReportLibrary From(IConfiguration configuration)
        => new(configuration[ConfigurationKey] is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, "reports"));

    public string Directory => directory;

    public IReadOnlyList<ReportSummary> Published()
    {
        if (!System.IO.Directory.Exists(directory))
            return [];

        var reports = new List<ReportSummary>();
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*" + ReportMarkdown.FileSuffix))
        {
            var name = Path.GetFileName(path);
            var month = name[..^ReportMarkdown.FileSuffix.Length];
            if (!ReportMarkdown.IsMonth(month))
                continue;

            var report = ReportMarkdown.Parse(month, File.ReadAllText(path));
            if (report.Published)
                reports.Add(new ReportSummary(month, report.Title));
        }

        return reports.OrderByDescending(r => r.Month, StringComparer.Ordinal).ToList();
    }

    public ReportDocument? Get(string month)
    {
        // The month is the only part of the path a request supplies, and it is four digits, a
        // hyphen and two: nothing a request sends can name another file.
        if (!ReportMarkdown.IsMonth(month))
            return null;

        var path = Path.Combine(directory, month + ReportMarkdown.FileSuffix);
        if (!File.Exists(path))
            return null;

        var report = ReportMarkdown.Parse(month, File.ReadAllText(path));
        return report.Published ? new ReportDocument(month, report.Title, report.Html, report.Summary) : null;
    }
}

/// <summary>One report file, read: whether it is published, its title, its body as HTML and its summary paragraph as plain text.</summary>
public sealed record ParsedReport(bool Published, string Title, string Html, string Summary = "");

/// <summary>
/// Markdown to HTML for the reports, with raw HTML disabled: a <c>&lt;script&gt;</c> or an
/// <c>&lt;iframe&gt;</c> in a report is printed as text, and a link whose address is not
/// http, https, mailto or on this site keeps its text and loses its address. A report says
/// what was measured; it cannot add markup to the page it is read on.
/// </summary>
public static partial class ReportMarkdown
{
    public const string FileSuffix = "-nashville.md";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Front matter, pipe tables (the skill's report is three tables), emphasis extras and
    /// autolinks; no raw HTML.
    /// </summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthPattern();

    public static bool IsMonth(string? month) => month is not null && MonthPattern().IsMatch(month);

    /// <summary>"September 2026" for <c>2026-09</c>.</summary>
    public static string MonthName(string month)
        => DateTime.TryParseExact(month, "yyyy-MM", Invariant, DateTimeStyles.None, out var first)
            ? first.ToString("MMMM yyyy", Invariant)
            : month;

    public static ParsedReport Parse(string month, string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var front = FrontMatter(document);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url))
                link.Url = null;
        }

        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!link.IsEmail && !IsSafeUrl(link.Url))
                link.Url = string.Empty;
        }

        var title = front.TryGetValue("title", out var t) && t.Length > 0
            ? t
            : document.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1) is { Inline: { } inline }
                ? PlainText(inline)
                : $"Nashville on-sale report, {MonthName(month)}";

        var published = front.TryGetValue("published", out var flag) && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        // The skill's fixed shape opens with a one-paragraph summary: the first paragraph at the
        // top level, after the front matter and the title.
        var summary = document.OfType<ParagraphBlock>().FirstOrDefault() is { Inline: { } first }
            ? PlainText(first)
            : string.Empty;

        using var writer = new StringWriter(Invariant);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return new ParsedReport(published, title, writer.ToString(), summary);
    }

    /// <summary>
    /// The front matter's <c>key: value</c> lines. Flat on purpose: the report's front matter
    /// is a flag and a title, and nothing here needs a YAML library to read those.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FrontMatter(MarkdownDocument document)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is not { } block)
            return values;

        foreach (var line in block.Lines.Lines.Take(block.Lines.Count))
        {
            var text = line.Slice.ToString().Trim();
            if (text.Length == 0 || text == "---" || text.StartsWith('#'))
                continue;

            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;

            var value = text[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];

            values[text[..colon].Trim()] = value;
        }

        return values;
    }

    /// <summary>http, https and mailto, or an address on this site. Anything else — javascript:, data:, vbscript: — is dropped.</summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        var trimmed = url.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('#') || trimmed.StartsWith('?'))
            return true;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "http" or "https" or "mailto";

        // Relative with no scheme, such as "2026-09": a colon before any slash would be one.
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return colon < 0 || (slash >= 0 && slash < colon);
    }

    private static string PlainText(ContainerInline inline)
    {
        var parts = new List<string>();
        foreach (var child in inline.Descendants())
        {
            switch (child)
            {
                case LiteralInline literal:
                    parts.Add(literal.Content.ToString());
                    break;
                case CodeInline code:
                    parts.Add(code.Content);
                    break;
                case LineBreakInline:
                    parts.Add(" ");
                    break;
            }
        }

        return string.Concat(parts).Trim();
    }
}
