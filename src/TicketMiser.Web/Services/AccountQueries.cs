using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Who is signed in to /account and what it watches. What it logged is the ledger's
/// (<see cref="Accounts.IAccountLedger"/>), read through the same <see cref="PurchaseQueries"/>
/// the desk grades with, so the page and the desk cannot disagree about a purchase.
/// </summary>
/// <param name="Watching">Events the account has an enabled watch on, soonest first.</param>
public sealed record AccountSummary(Account Account, IReadOnlyList<Event> Watching);

/// <summary>The account's own reads for the static pages. An interface so a render test can hand a page its answer.</summary>
public interface IAccountQueries
{
    /// <summary>Null when the account no longer exists: the caller signs the cookie out.</summary>
    Task<AccountSummary?> SummaryAsync(int accountId, CancellationToken ct = default);

    /// <summary>Whether the account has an enabled watch on the event: the record page's "Watching".</summary>
    Task<bool> IsWatchingAsync(int accountId, int eventId, CancellationToken ct = default);
}

/// <summary>
/// Reads for one account, named by id because the pages that call it know whose they are
/// drawing. The ownership rule is still <see cref="Ownership"/>'s: this class asks it for the
/// account's scope rather than filtering by owner itself.
/// </summary>
public sealed class AccountQueries(IDbContextFactory<TicketMiserDbContext> factory) : IAccountQueries
{
    public async Task<AccountSummary?> SummaryAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
            return null;

        var owner = OwnerScope.ForAccount(accountId);

        var watching = (await db.Watches
            .AsNoTracking()
            .VisibleTo(owner)
            .Include(w => w.Event!).ThenInclude(e => e.Venue)
            .Where(w => w.Enabled)
            .OrderBy(w => w.Event!.StartsAt)
            .ToListAsync(ct))
            .Select(w => w.Event!)
            .ToList();

        return new AccountSummary(account, watching);
    }

    public async Task<bool> IsWatchingAsync(int accountId, int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Watches.VisibleTo(OwnerScope.ForAccount(accountId)).AnyAsync(w => w.Enabled && w.EventId == eventId, ct);
    }
}
