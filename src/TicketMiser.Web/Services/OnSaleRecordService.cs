using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Reads one event's on-sale record for the desk. An interface so a render test can hand the
/// panel a record without a database behind it.
/// </summary>
public interface IOnSaleRecordService
{
    /// <summary>The record, or null when no event has that id.</summary>
    Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default);
}

/// <summary>
/// The query behind the Event window. A factory rather than a scoped context because a Blazor
/// Server circuit lives for hours and a context that lived with it would hold every event the
/// operator ever opened; each read gets its own and lets it go.
/// </summary>
public sealed class OnSaleRecordService(IDbContextFactory<TicketMiserDbContext> factory) : IOnSaleRecordService
{
    public async Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var evt = await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

        if (evt is null)
            return null;

        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => t.EventId == eventId)
            .OrderBy(t => t.MinutesFromOnSale)
            .ThenBy(t => t.ObservedAt)
            .ToListAsync(ct);

        var sourceIds = ticks.Select(t => t.SourceId).Distinct().ToList();

        var sources = await db.Sources
            .AsNoTracking()
            .Where(s => sourceIds.Contains(s.Id))
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

        return new OnSaleRecord(evt, sources, ticks, OnSaleRecord.Derive(ticks, sources));
    }
}

/// <summary>
/// Where a price cell sends the reader: the source's own page for the event, so every number
/// on the desk can be checked against the site that published it.
///
/// <para>
/// A <see cref="Source.BaseUrl"/> is an API root and no place to send a person, so the link is
/// built from the event's id on the source's consumer site instead, and falls back to that site
/// when the event has no id there. The affiliate tagging Phase 6 adds lands here.
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
