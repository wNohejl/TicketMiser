using System.Globalization;
using System.Text;

namespace TicketMiser.Core.Entities;

/// <summary>
/// The permanent address of an event's on-sale record: <c>{performer-or-name}-{venue}-{yyyy-mm-dd}</c>,
/// lower-cased ASCII, the date being the night in the venue's own zone.
///
/// <para>
/// Deterministic on purpose, so two machines resolving the same event from the same feed
/// mint the same address, and so a link shared before the other machine has seen the event
/// still resolves once it has. When two events would share one, the second takes a numeric
/// suffix; a slug once assigned is never recomputed, because the URL is the product.
/// </para>
/// </summary>
public static class EventSlug
{
    public const int MaxLength = 160;

    /// <summary>The base slug, before de-duplication. Never empty: an event with no usable characters is "event".</summary>
    public static string Base(Event evt)
        => Base(evt.Performer?.Name, evt.Name, evt.Venue?.Name, evt.StartsAt, evt.Venue?.Timezone);

    /// <summary>See <see cref="Base(Event)"/>. The pure form, for callers that have the parts and not the row.</summary>
    public static string Base(string? performer, string name, string? venue, DateTimeOffset startsAt, string? timezone)
    {
        var who = Fold(performer is { Length: > 0 } ? performer : name);
        var where = Fold(venue ?? string.Empty);
        var when = LocalDate(startsAt, timezone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Room for a "-99" suffix inside the column. A long bill is cut, on a word where there
        // is one, and never the room or the night: those are what make the address an address.
        var budget = MaxLength - 4 - when.Length - 1 - (where.Length > 0 ? where.Length + 1 : 0);
        if (budget > 0 && who.Length > budget)
        {
            var cut = who.LastIndexOf('-', budget - 1);
            who = who[..(cut > budget / 2 ? cut : budget)];
        }

        var parts = new[] { who, where, when }.Where(p => p.Length > 0);
        var slug = string.Join('-', parts);

        // A room whose own name fills the column is not a Nashville room, but the column holds.
        if (slug.Length > MaxLength - 4)
            slug = slug[..(MaxLength - 4)].TrimEnd('-');

        return slug.Length == 0 ? "event" : slug;
    }

    /// <summary>
    /// The base, or the base with the lowest numeric suffix from 2 upward that is not in
    /// <paramref name="taken"/>. Comparison is ordinal: slugs are already folded.
    /// </summary>
    public static string Unique(string @base, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(@base))
            return @base;

        for (var n = 2; ; n++)
        {
            var candidate = $"{@base}-{n.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Lower-cased ASCII with runs of anything else collapsed to one hyphen. Accents are
    /// decomposed and dropped, so "Beyoncé" folds to "beyonce" rather than "beyonc"; an
    /// ampersand reads as "and" because "3rd & Lindsley" is a venue and "3rd-lindsley" is not.
    /// </summary>
    public static string Fold(string text)
    {
        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingHyphen = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (c < 128 && char.IsAsciiLetterOrDigit(c))
            {
                if (pendingHyphen && sb.Length > 0)
                    sb.Append('-');

                pendingHyphen = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return sb.ToString();
    }

    private static DateTime LocalDate(DateTimeOffset startsAt, string? timezone)
    {
        if (timezone is { Length: > 0 })
        {
            try
            {
                return TimeZoneInfo.ConvertTime(startsAt, TimeZoneInfo.FindSystemTimeZoneById(timezone)).Date;
            }
            catch (TimeZoneNotFoundException)
            {
                // An unresolvable zone is not a reason to have no address; UTC is the honest fallback.
            }
        }

        return startsAt.UtcDateTime.Date;
    }
}
