namespace TicketMiser.Core.Entities;

/// <summary>
/// How two sources' names for one room are compared.
///
/// <para>
/// SeatGeek appends the town to a room's name ("The Truth - Nashville") and Ticketmaster
/// sometimes appends the state ("The Pinnacle - TN"); the room is the same. <see cref="Key"/>
/// strips a trailing locality before folding, but only the locality the source itself gave
/// for the room: "City Winery - Memphis" in Nashville keeps its suffix, and a room whose name
/// merely contains the town ("Nashville Municipal Auditorium", "Brooklyn Bowl Nashville") is
/// untouched because nothing separates the town from the name.
/// </para>
/// </summary>
public static class VenueName
{
    private static readonly char[] Dashes = ['-', '–', '—'];

    /// <summary>Lower-case letters and digits only: the comparison the resolver has always made.</summary>
    public static string Normalise(string name)
        => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The name with its own trailing locality removed, then folded by <see cref="Normalise"/>.</summary>
    public static string Key(string name, string? city, string? state)
        => Normalise(StripLocality(name, city, state));

    /// <summary>
    /// Removes one trailing " - City", ", City", " (City)", " - City, ST" or the same with the
    /// state alone, where City and ST are the room's own. Returns the name unchanged when no
    /// such suffix is present or when removing it would leave nothing.
    /// </summary>
    public static string StripLocality(string name, string? city, string? state)
    {
        var trimmed = name.Trim();

        foreach (var locality in Localities(city?.Trim(), state?.Trim()))
        {
            if (TryStrip(trimmed, locality, out var room))
                return room;
        }

        return trimmed;
    }

    private static IEnumerable<string> Localities(string? city, string? state)
    {
        var hasCity = !string.IsNullOrEmpty(city);
        var hasState = !string.IsNullOrEmpty(state);

        // Longest first, so "Nashville, TN" is taken whole rather than leaving "Nashville,".
        if (hasCity && hasState)
        {
            yield return $"{city}, {state}";
            yield return $"{city},{state}";
            yield return $"{city} {state}";
        }

        if (hasCity)
            yield return city!;

        if (hasState)
            yield return state!;
    }

    private static bool TryStrip(string name, string locality, out string room)
    {
        room = name;

        // " (Nashville)" / "(Nashville, TN)"
        var bracketed = $"({locality})";
        if (name.EndsWith(bracketed, StringComparison.OrdinalIgnoreCase))
            return Accept(name[..^bracketed.Length], out room);

        if (!name.EndsWith(locality, StringComparison.OrdinalIgnoreCase))
            return false;

        var head = name[..^locality.Length].TrimEnd();
        if (head.Length == 0)
            return false;

        // A separator is required: "Brooklyn Bowl Nashville" is a name, not a name and a town.
        var last = head[^1];
        if (last == ',' || Array.IndexOf(Dashes, last) >= 0)
        {
            // "Foo-Nashville" with no space is as likely a hyphenated name; require the gap.
            if (last != ',' && head.Length == name.Length - locality.Length)
                return false;

            return Accept(head[..^1], out room);
        }

        return false;
    }

    private static bool Accept(string head, out string room)
    {
        room = head.TrimEnd();
        return Normalise(room).Length > 0;
    }
}
