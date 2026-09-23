using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// An account's watch counts as a confirmed subscription for delivery. It is made one: a
/// <see cref="Subscription"/> row for the account's address on the event, confirmed (following
/// the sign-in link proved the address) and carrying the unsubscribe token every alert email
/// needs. Because a subscription is one row per address per event, an address that both
/// subscribed on the record page and watches the event while signed in is still one row, one
/// <see cref="AlertDelivery"/>, one email.
/// </summary>
public static class AlertSubscriptions
{
    /// <summary>The subscription an account's watch stands for: confirmed now, never emailed a confirmation.</summary>
    public static Subscription ForAccount(Account account, int eventId, DateTimeOffset now) => new()
    {
        Email = account.Email,
        EventId = eventId,
        AccountId = account.Id,
        CreatedAt = now,
        ConfirmedAt = now,
        ConfirmToken = SubscriptionService.NewToken(),
        UnsubscribeToken = SubscriptionService.NewToken()
    };

    /// <summary>
    /// Before an alert on <paramref name="eventId"/> is delivered: every enabled owned watch on
    /// the event whose address has no subscription row there gets one. An address with a row
    /// keeps it as it is — an unsubscribe is honoured, never undone here — so this only fills
    /// the gap for a watch kept by some path that did not make the row itself (the desk, or a
    /// watch older than the rule). Returns how many rows were made.
    /// </summary>
    public static async Task<int> EnsureForOwnedWatchesAsync(TicketMiserDbContext db, int eventId, DateTimeOffset now, CancellationToken ct)
    {
        var missing = await db.Watches
            .Where(w => w.EventId == eventId && w.Enabled && w.OwnerId != null)
            .Select(w => w.Owner!)
            .Where(a => !db.Subscriptions.Any(s => s.EventId == eventId && s.Email == a.Email))
            .Distinct()
            .ToListAsync(ct);

        if (missing.Count == 0)
            return 0;

        foreach (var account in missing)
            db.Subscriptions.Add(ForAccount(account, eventId, now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
        {
            // Someone subscribed or watched in between; their row is the one that counts.
            foreach (var entry in db.ChangeTracker.Entries<Subscription>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return 0;
        }

        return missing.Count;
    }
}
