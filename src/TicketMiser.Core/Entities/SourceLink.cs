namespace TicketMiser.Core.Entities;

/// <summary>
/// Where a price cell sends the reader: the source's own page for the event, so every number
/// on the desk can be checked against the site that published it.
///
/// <para>
/// A <see cref="Source.BaseUrl"/> is an API root and no place to send a person, so the link is
/// built from the event's id on the source's consumer site instead, and falls back to that site
/// when the event has no id there. The affiliate tagging Phase 6 adds lands here.
/// </para>
///
/// <para>
/// In Core rather than the web host because an alert email cites the same page a price cell
/// does, and the reliability layer that sends it has no reference to the web host.
/// </para>
/// </summary>
public static class SourceLink
{
    /// <summary>The event on the source's site, or the site itself when the event has no id there.</summary>
    public static string For(Source source, Event evt)
    {
        if (SiteFor(source) is not { } site)
            return source.BaseUrl;

        var idKey = source.Key.StartsWith("ticketmaster", StringComparison.Ordinal) ? "ticketmaster" : source.Key;

        return evt.ExternalIds.TryGetValue(idKey, out var id) && id.Length > 0
            ? site.Event(Uri.EscapeDataString(id))
            : site.Root;
    }

    private static (string Root, Func<string, string> Event)? SiteFor(Source source)
        => source.Key switch
        {
            "seatgeek" => ("https://seatgeek.com", id => $"https://seatgeek.com/e/events/{id}"),
            var k when k.StartsWith("ticketmaster", StringComparison.Ordinal)
                => ("https://www.ticketmaster.com", id => $"https://www.ticketmaster.com/event/{id}"),
            _ => null
        };
}
