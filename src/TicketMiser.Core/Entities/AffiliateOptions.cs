namespace TicketMiser.Core.Entities;

/// <summary>
/// The affiliate programmes a price cell's link may be tagged for, bound from the configuration
/// section <c>Affiliates</c> (<c>Affiliates__ticketmaster__Enabled</c> and so on in the
/// environment). Everything is empty by default, and an unconfigured programme tags nothing:
/// <see cref="SourceLink.Tagged"/> then returns the plain canonical link.
///
/// <para>
/// Both programmes run on Impact, whose deep link is
/// <c>https://{tracking domain}/c/{partner id}/{ad id}/{campaign id}?u={encoded destination}</c>.
/// Impact's "campaign id" is the brand's programme, so it is <see cref="AffiliateProgram.ProgramId"/>
/// here and the middle segment is <see cref="AffiliateProgram.AdId"/>. The tracking domains are
/// ticketmaster.evyy.net and seatgeek.pxf.io; the template is configuration rather than code so a
/// changed domain or an added sub-id is an environment change.
/// </para>
///
/// <para>
/// Rule 6 of docs/legal-guidelines.md: the Ticketmaster developer terms forbid deriving revenue
/// from the API outside Ticketmaster's own programmes. Until a partner agreement or written
/// clarification exists, the Ticketmaster affiliate programme on Impact is the only Ticketmaster
/// revenue path, and this mechanism adds no other: it tags a Ticketmaster link for Ticketmaster's
/// programme and nothing else, never routes a Ticketmaster price to another seller's link, and
/// carries no paid tier.
/// </para>
///
/// <para>
/// Rule 7: wherever a tagged link can render on a public page, the page says in plain words that
/// the link may earn TicketMiser a commission (<see cref="Disclosure"/>).
/// </para>
///
/// <para>
/// Before enabling Ticketmaster: its affiliate team is reported (a third-party issue tracker,
/// 2026-09-10, not yet read from Ticketmaster itself) to have asked affiliates to stop rendering
/// affiliate URLs as static HTML, because crawlers were firing click events. The public record
/// page is scriptless static HTML, and its tagged anchors carry <c>rel="sponsored nofollow"</c>
/// but are still in the markup. Confirm the requirement in the Impact programme terms first.
/// </para>
/// </summary>
public sealed class AffiliateOptions
{
    public const string SectionName = "Affiliates";

    /// <summary>What a public page says near a tagged link (rule 7). Plain words, no hedging.</summary>
    public const string Disclosure =
        "Links to ticket sellers on this page may earn TicketMiser a commission if you buy through them. It costs you nothing extra.";

    /// <summary>Ticketmaster's programme on Impact. Covers every Ticketmaster source (Discovery, Inventory Status, resale).</summary>
    public AffiliateProgram Ticketmaster { get; set; } = new();

    /// <summary>SeatGeek's partner programme on Impact.</summary>
    public AffiliateProgram SeatGeek { get; set; } = new();

    /// <summary>The programme a source's links belong to, or null for a source with none.</summary>
    public AffiliateProgram? For(Source source)
        => source.Key switch
        {
            "seatgeek" => SeatGeek,
            var k when k.StartsWith("ticketmaster", StringComparison.Ordinal) => Ticketmaster,
            _ => null
        };

    /// <summary>Whether any programme would tag a link: the condition for the disclosure.</summary>
    public bool AnyConfigured => Ticketmaster.IsConfigured || SeatGeek.IsConfigured;
}

/// <summary>One Impact programme: switched on, a deep-link template, and the ids it is filled with.</summary>
public sealed class AffiliateProgram
{
    /// <summary>Off by default. On with a template missing an id still tags nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The deep link, e.g. <c>https://ticketmaster.evyy.net/c/{publisherId}/{adId}/{programId}?u={url}</c>.
    /// <c>{url}</c> is required and receives the canonical destination percent-encoded once; the
    /// id placeholders are optional, but each one the template names must be configured.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The publisher's Impact partner (account) id: the first path segment.</summary>
    public string PublisherId { get; set; } = string.Empty;

    /// <summary>The Impact ad id: the second path segment.</summary>
    public string AdId { get; set; } = string.Empty;

    /// <summary>The brand's programme, which Impact calls the campaign id: the third path segment.</summary>
    public string ProgramId { get; set; } = string.Empty;

    private static readonly (string Placeholder, Func<AffiliateProgram, string> Value)[] Ids =
    [
        ("{publisherId}", p => p.PublisherId),
        ("{adId}", p => p.AdId),
        ("{programId}", p => p.ProgramId)
    ];

    /// <summary>
    /// On, with a template that takes the destination, every id it names filled in, and an
    /// absolute https result. Anything less tags nothing rather than publishing a broken link.
    /// </summary>
    public bool IsConfigured
        => Enabled
           && Template.Contains("{url}", StringComparison.Ordinal)
           && Ids.All(id => !Template.Contains(id.Placeholder, StringComparison.Ordinal) || id.Value(this).Trim().Length > 0)
           && Uri.TryCreate(Fill("https://example.invalid/"), UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The tracking link for <paramref name="destination"/>, or null when the programme is not configured.</summary>
    public string? Tag(string destination) => IsConfigured ? Fill(destination) : null;

    private string Fill(string destination)
    {
        var link = Template;

        foreach (var (placeholder, value) in Ids)
            link = link.Replace(placeholder, Uri.EscapeDataString(value(this).Trim()), StringComparison.Ordinal);

        return link.Replace("{url}", Uri.EscapeDataString(destination), StringComparison.Ordinal);
    }
}
