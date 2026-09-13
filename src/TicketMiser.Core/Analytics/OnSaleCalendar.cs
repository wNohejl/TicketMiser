using System.Globalization;
using System.Text;
using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>Which moment of a sale an entry marks.</summary>
public enum OnSaleEntryKind
{
    Presale,
    Public
}

/// <summary>
/// One moment on the Nashville on-sale calendar: a presale opening or the public on-sale of
/// one event. The instant is the source's; the zone is the venue's, because that is the zone
/// the sale was announced in and the zone a fan sets an alarm by.
/// </summary>
public sealed record OnSaleEntry(
    Event Event,
    OnSaleEntryKind Kind,
    string? PresaleName,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt)
{
    public string Zone => Event.Venue?.Timezone ?? "UTC";

    /// <summary>What the entry is called on a page or in a calendar: the moment, then the event and the room.</summary>
    public string Summary
    {
        get
        {
            var room = Event.Venue is { } v ? $" at {v.Name}" : string.Empty;
            return Kind == OnSaleEntryKind.Public
                ? $"On sale: {Event.Name}{room}"
                : $"Presale{(string.IsNullOrWhiteSpace(PresaleName) ? "" : $" ({PresaleName})")}: {Event.Name}{room}";
        }
    }

    /// <summary>A stable identity for the calendar client, so a refreshed feed updates an entry rather than duplicating it.</summary>
    public string Uid => Kind == OnSaleEntryKind.Public
        ? $"event-{Event.Id}-onsale@ticketmiser"
        : $"event-{Event.Id}-presale-{Slugify(PresaleName)}-{StartsAt.UtcDateTime:yyyyMMddTHHmm}@ticketmiser";

    private static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');

        return sb.ToString().Trim('-');
    }
}

/// <summary>
/// The Nashville on-sale calendar, built from what the feed already recorded on every event:
/// the public on-sale time and the presale windows. Costs no quota, which is why it is the
/// product's cheapest offer (roadmap §3, offer B). Pure, so the page and the feed are tested
/// without a database.
/// </summary>
public static class OnSaleCalendar
{
    /// <summary>
    /// Every presale opening and public on-sale inside the window, oldest first, then by event
    /// name so two sales at the same minute have a stable order. An event whose on-sale is to
    /// be announced contributes nothing: a calendar entry with no time is not an entry.
    /// </summary>
    public static IReadOnlyList<OnSaleEntry> Build(IEnumerable<Event> events, DateTimeOffset from, DateTimeOffset to)
    {
        var entries = new List<OnSaleEntry>();

        foreach (var evt in events)
        {
            if (evt.OnSaleAt is { } onSale && !evt.OnSaleTbd && onSale >= from && onSale <= to)
                entries.Add(new OnSaleEntry(evt, OnSaleEntryKind.Public, null, onSale, null));

            foreach (var presale in EventPresale.Parse(evt.Presales))
            {
                if (presale.StartsAt is { } start && start >= from && start <= to)
                    entries.Add(new OnSaleEntry(evt, OnSaleEntryKind.Presale, presale.Name, start, presale.EndsAt));
            }
        }

        return entries
            .OrderBy(e => e.StartsAt)
            .ThenBy(e => e.Event.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Kind)
            .ToList();
    }

    /// <summary>The instant in the entry's zone, for a page or a calendar description.</summary>
    public static DateTimeOffset InZone(DateTimeOffset instant, string zone)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone));
        }
        catch (TimeZoneNotFoundException)
        {
            return instant;
        }
        catch (InvalidTimeZoneException)
        {
            return instant;
        }
    }
}

/// <summary>
/// Writes the calendar as iCalendar (RFC 5545) for a subscription. Times are written in UTC,
/// which every client converts to the reader's own zone, and the description repeats the
/// time in the venue's zone so a fan travelling still sees the hour the sale was announced
/// for. Lines are folded at 75 octets and text is escaped as the RFC asks, because a feed
/// that a strict client rejects is not a feed.
/// </summary>
public static class OnSaleIcs
{
    public const string ContentType = "text/calendar; charset=utf-8";

    /// <summary>How long a client should wait before refreshing: the feed changes once a day, when discovery runs.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    public static string Write(
        IReadOnlyList<OnSaleEntry> entries,
        string calendarName,
        string siteBase,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();

        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//TicketMiser//Nashville on-sales//EN");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, $"X-WR-CALNAME:{Escape(calendarName)}");
        Line(sb, "X-WR-TIMEZONE:America/Chicago");
        Line(sb, $"REFRESH-INTERVAL;VALUE=DURATION:PT{(int)RefreshInterval.TotalHours}H");
        Line(sb, $"X-PUBLISHED-TTL:PT{(int)RefreshInterval.TotalHours}H");

        foreach (var entry in entries)
        {
            var end = entry.EndsAt ?? entry.StartsAt.AddMinutes(15);
            if (end <= entry.StartsAt)
                end = entry.StartsAt.AddMinutes(15);

            Line(sb, "BEGIN:VEVENT");
            Line(sb, $"UID:{entry.Uid}");
            Line(sb, $"DTSTAMP:{Stamp(now)}");
            Line(sb, $"DTSTART:{Stamp(entry.StartsAt)}");
            Line(sb, $"DTEND:{Stamp(end)}");
            Line(sb, $"SUMMARY:{Escape(entry.Summary)}");
            Line(sb, $"DESCRIPTION:{Escape(Description(entry, siteBase))}");
            if (entry.Event.Venue is { } venue)
                Line(sb, $"LOCATION:{Escape(Location(venue))}");
            if (entry.Event.Slug is { Length: > 0 } slug)
                Line(sb, $"URL:{siteBase.TrimEnd('/')}/e/{slug}");
            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    private static string Description(OnSaleEntry entry, string siteBase)
    {
        var local = OnSaleCalendar.InZone(entry.StartsAt, entry.Zone);
        var when = $"{local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} {entry.Zone}";
        var show = OnSaleCalendar.InZone(entry.Event.StartsAt, entry.Zone)
            .ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

        var lines = new List<string>
        {
            entry.Kind == OnSaleEntryKind.Public ? $"Public on-sale opens {when}." : $"Presale opens {when}.",
            $"Show: {show}."
        };

        if (entry.Event.Slug is { Length: > 0 } slug)
            lines.Add($"On-sale record: {siteBase.TrimEnd('/')}/e/{slug}");

        lines.Add("Times from the ticketing source's own listing; nothing scraped.");

        return string.Join("\n", lines);
    }

    private static string Location(Venue venue)
        => string.Join(", ", new[] { venue.Name, venue.City, venue.State }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC 5545 §3.3.11: backslash, semicolon and comma are escaped; a newline is written as <c>\n</c>.</summary>
    public static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    /// <summary>RFC 5545 §3.1: a content line is at most 75 octets; the rest continues on lines that begin with one space.</summary>
    private static void Line(StringBuilder sb, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= 75)
        {
            sb.Append(content).Append("\r\n");
            return;
        }

        var first = true;
        var index = 0;
        while (index < content.Length)
        {
            var limit = first ? 75 : 74;
            var take = 0;
            var octets = 0;
            while (index + take < content.Length)
            {
                var size = Encoding.UTF8.GetByteCount(content.AsSpan(index + take, 1));
                if (octets + size > limit)
                    break;
                octets += size;
                take++;
            }

            sb.Append(first ? "" : " ").Append(content, index, take).Append("\r\n");
            index += take;
            first = false;
        }
    }
}
