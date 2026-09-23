using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Reads one event's on-sale record, for the desk's Event window and for the public page at
/// <c>/e/{slug}</c>. An interface so a render test can hand either a record without a database
/// behind it.
/// </summary>
public interface IOnSaleRecordService
{
    /// <summary>The record, or null when no event has that id.</summary>
    Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default);

    /// <summary>The record, or null when no event has that slug.</summary>
    Task<OnSaleRecord?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>The event's public address, or null when no event has that id or it has not been addressed yet.</summary>
    Task<string?> SlugForAsync(int eventId, CancellationToken ct = default);
}

/// <summary>
/// The query behind the Event window. A factory rather than a scoped context because a Blazor
/// Server circuit lives for hours and a context that lived with it would hold every event the
/// operator ever opened; each read gets its own and lets it go.
/// </summary>
public sealed class OnSaleRecordService(IDbContextFactory<TicketMiserDbContext> factory) : IOnSaleRecordService
{
    public Task<OnSaleRecord?> GetAsync(int eventId, CancellationToken ct = default)
        => ReadAsync(e => e.Id == eventId, ct);

    public Task<OnSaleRecord?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => ReadAsync(e => e.Slug == slug, ct);

    public async Task<string?> SlugForAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => e.Slug)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<OnSaleRecord?> ReadAsync(
        System.Linq.Expressions.Expression<Func<Event, bool>> which, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var evt = await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .FirstOrDefaultAsync(which, ct);

        if (evt is null)
            return null;

        var eventId = evt.Id;

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
