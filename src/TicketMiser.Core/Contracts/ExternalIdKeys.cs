using System.Text.RegularExpressions;

namespace TicketMiser.Core.Contracts;

/// <summary>
/// Keys in <c>Event.ExternalIds</c> that name an id without being a source's own key.
///
/// <para>
/// Ticketmaster has two ids per event. The Discovery API's (<c>G5viZ…</c>, <c>Z7r9j…</c>) is
/// the one every Ticketmaster call takes and lives under the <c>ticketmaster</c> key. The older
/// host id, sixteen upper-case hex digits (<c>1B00650D13C6A27C</c>), is the last segment of the
/// event page URL and is the only Ticketmaster id SeatGeek gives. It cannot be fetched, so it
/// lives under its own key: filed under <c>ticketmaster</c> it was sent to the Discovery API as
/// if it were an event id, and it made the resolver believe Ticketmaster had already named a
/// different event, which split one show into two.
/// </para>
/// </summary>
public static partial class ExternalIdKeys
{
    public const string TicketmasterLegacy = "ticketmaster-legacy";

    /// <summary>Whether a value has the shape of Ticketmaster's legacy host id.</summary>
    public static bool IsTicketmasterLegacyId(string? value)
        => value is { Length: 16 } && LegacyId().IsMatch(value);

    /// <summary>The legacy id at the end of a Ticketmaster event page URL, or null.</summary>
    public static string? TicketmasterLegacyIdFromUrl(string? url)
    {
        if (url is null)
            return null;

        var match = LegacyInUrl().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("^[0-9A-F]{16}$")]
    private static partial Regex LegacyId();

    [GeneratedRegex(@"/event/([0-9A-F]{16})(?:[/?#]|$)")]
    private static partial Regex LegacyInUrl();
}
