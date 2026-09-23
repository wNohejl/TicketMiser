using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Reads the on-sale calendar: every presale opening and public on-sale the feed has
/// recorded on a Nashville event inside a window. An interface so the page and the feed can
/// be rendered in a test from a handful of events.
/// </summary>
public interface IOnSaleCalendarService
{
    /// <summary>How far ahead the calendar looks: the feed lists on-sales weeks out, rarely more than three months.</summary>
    static readonly TimeSpan Horizon = TimeSpan.FromDays(90);

    /// <summary>How far back an entry stays on the page after it opened, so a sale that opened this morning is still there this afternoon.</summary>
    static readonly TimeSpan Grace = TimeSpan.FromHours(12);

    Task<IReadOnlyList<OnSaleEntry>> LoadAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed class OnSaleCalendarService(IDbContextFactory<TicketMiserDbContext> factory) : IOnSaleCalendarService
{
    public async Task<IReadOnlyList<OnSaleEntry>> LoadAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var from = now - IOnSaleCalendarService.Grace;
        var to = now + IOnSaleCalendarService.Horizon;

        await using var db = await factory.CreateDbContextAsync(ct);

        // Presales live in a JSON column, so the row filter is the public on-sale or a future
        // show, and the presale windows are read on the way through Build. A cancelled show
        // has no sale to go to.
        var events = await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Where(e => e.Status != EventStatus.Cancelled
                        && e.StartsAt > now
                        && ((e.OnSaleAt != null && e.OnSaleAt >= from && e.OnSaleAt <= to) || e.Presales != "[]"))
            .ToListAsync(ct);

        return OnSaleCalendar.Build(events, from, to);
    }
}
