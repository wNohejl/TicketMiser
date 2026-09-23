using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Reliability.Accounts;

public enum SignInRequestOutcome
{
    /// <summary>A link is on its way, or one went out a moment ago, or the address is new. The caller says the same thing in every case.</summary>
    Accepted,

    /// <summary>Not an address: nothing stored, nothing sent.</summary>
    InvalidAddress
}

/// <summary>What following a watch did: the event's slug for the redirect, or null when no event has the id.</summary>
public record WatchResult(int EventId, string? Slug);

/// <summary>
/// Magic-link sign-in, and the two things an account does on its own behalf that the scheduler
/// and the alert email then act on: keeping a watch, and inheriting the subscriptions its
/// address confirmed before it signed in.
///
/// <para>
/// No password anywhere. A sign-in posts an address; the address gets a link carrying a
/// 32-byte random secret; only the secret's SHA-256 is stored, it lasts fifteen minutes, and
/// it signs in once. The account is created when the link is followed — an address that
/// never follows one never becomes a row. The caller answers every post the same way, so the
/// form does not reveal who has an account.
/// </para>
/// </summary>
public class AccountService(
    TicketMiserDbContext db,
    INotifier notifier,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<AccountService> logger)
{
    /// <summary>A second link to one address inside this gap is not sent: the form cannot be used to flood an inbox.</summary>
    public static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(1);

    /// <summary>Spent and expired links are kept this long, then deleted the next time anyone asks for one.</summary>
    public static readonly TimeSpan KeepSpentFor = TimeSpan.FromDays(1);

    private readonly NotificationOptions _options = options.Value;

    /// <summary>Lowercase hex SHA-256 of the secret: what the table holds instead of it.</summary>
    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<SignInRequestOutcome> RequestSignInAsync(string? emailInput, CancellationToken ct = default)
    {
        if (SubscriptionService.NormaliseEmail(emailInput) is not { } email)
            return SignInRequestOutcome.InvalidAddress;

        var now = clock.GetUtcNow();

        // Housekeeping on the way in: a link nobody can use any more is nothing to keep.
        var cutoff = now - KeepSpentFor;
        await db.SignInTokens.Where(t => t.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);

        var recent = now - ResendAfter;
        if (await db.SignInTokens.AnyAsync(t => t.Email == email && t.CreatedAt > recent && t.UsedAt == null, ct))
        {
            logger.LogInformation("Sign-in link not resent: one went to the same address under {Gap} ago", ResendAfter);
            return SignInRequestOutcome.Accepted;
        }

        var token = SubscriptionService.NewToken();
        var row = new SignInToken
        {
            Email = email,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now + SignInToken.Lifetime
        };
        db.SignInTokens.Add(row);
        await db.SaveChangesAsync(ct);

        try
        {
            await notifier.SendAsync(SignInMessage(email, token), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The link never left, so it must not count against the resend throttle.
            logger.LogError(ex, "Sign-in email {Id} failed", row.Id);
            db.SignInTokens.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return SignInRequestOutcome.Accepted;
    }

    /// <summary>Whether a link would sign someone in now, without spending it: the GET page reads this to show the button or say the link is dead.</summary>
    public async Task<bool> IsRedeemableAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow();
        return await db.SignInTokens.AnyAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
    }

    /// <summary>
    /// Spends a link and returns the account it signs in — created on first use — or null for a
    /// link that is unknown, expired or already used. Single use holds under a race: the link
    /// is marked used by one conditional update, and only the caller whose update matched a
    /// row goes on.
    /// </summary>
    public async Task<Account?> RedeemAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow();

        var spent = await db.SignInTokens
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

        if (spent != 1)
            return null;

        var email = await db.SignInTokens.Where(t => t.TokenHash == hash).Select(t => t.Email).SingleAsync(ct);

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null)
        {
            account = new Account { Email = email, CreatedAt = now };
            db.Accounts.Add(account);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
            {
                // Two links for one new address followed at once: the other made the row.
                db.Entry(account).State = EntityState.Detached;
                account = await db.Accounts.FirstAsync(a => a.Email == email, ct);
            }
        }

        account.LastSignInAt = now;
        await db.SaveChangesAsync(ct);

        await ClaimSubscriptionsAsync(account, ct);
        return account;
    }

    public Task<Account?> GetAsync(int accountId, CancellationToken ct = default)
        => db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);

    /// <summary>
    /// Phase 6's subscriptions become the account's: every confirmed, active subscription
    /// carrying the address is marked as the account's, and each of its events gets an owned
    /// watch unless the account already has one (an owned watch the account switched off stays
    /// off). The subscription rows stay — they carry the unsubscribe token every alert email
    /// is sent with. Returns how many watches were created.
    /// </summary>
    public async Task<int> ClaimSubscriptionsAsync(Account account, CancellationToken ct = default)
    {
        var subs = await db.Subscriptions
            .Where(s => s.Email == account.Email && s.ConfirmedAt != null && s.UnsubscribedAt == null)
            .ToListAsync(ct);

        if (subs.Count == 0)
            return 0;

        var eventIds = subs.Select(s => s.EventId).ToList();
        var owner = OwnerScope.ForAccount(account.Id);
        var watched = (await db.Watches.OwnedBy(owner).Where(w => eventIds.Contains(w.EventId)).Select(w => w.EventId).ToListAsync(ct)).ToHashSet();
        var now = clock.GetUtcNow();
        var created = 0;

        foreach (var sub in subs)
        {
            sub.AccountId = account.Id;

            if (watched.Add(sub.EventId))
            {
                db.Watches.Add(new Watch { EventId = sub.EventId, OwnerId = account.Id, CreatedAt = now });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);

        if (created > 0)
            logger.LogInformation("Account {AccountId} claimed {Count} subscriptions as watches", account.Id, created);

        return created;
    }

    /// <summary>
    /// The account watches the event: its own watch, new or switched back on, and an active
    /// alert subscription for its address on the event, so the "face value is back" email
    /// reaches it. Following the sign-in link proved the address, so the subscription needs no
    /// confirmation email; an address that had unsubscribed from this event is subscribed again
    /// with fresh secrets, because pressing Watch is asking again. Null when either the account
    /// or the event does not exist.
    /// </summary>
    public async Task<WatchResult?> WatchAsync(int accountId, int eventId, CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        var evt = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => new { e.Id, e.Slug }).FirstOrDefaultAsync(ct);
        if (account is null || evt is null)
            return null;

        var now = clock.GetUtcNow();
        var watch = await db.Watches.OwnedBy(OwnerScope.ForAccount(accountId)).FirstOrDefaultAsync(w => w.EventId == eventId, ct);
        if (watch is null)
            db.Watches.Add(new Watch { EventId = eventId, OwnerId = accountId, CreatedAt = now });
        else
            watch.Enabled = true;

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Email == account.Email && s.EventId == eventId, ct);
        if (sub is null)
        {
            db.Subscriptions.Add(AlertSubscriptions.ForAccount(account, eventId, now));
        }
        else
        {
            sub.AccountId = account.Id;
            sub.ConfirmedAt ??= now;

            if (sub.UnsubscribedAt is not null)
            {
                sub.UnsubscribedAt = null;
                sub.ConfirmToken = SubscriptionService.NewToken();
                sub.UnsubscribeToken = SubscriptionService.NewToken();
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
        {
            // A double-click: the other request made the rows, which is the state asked for.
            db.ChangeTracker.Clear();
        }

        return new WatchResult(evt.Id, evt.Slug);
    }

    /// <summary>The sign-in email: the link, how long it lasts, and what to do if it was not asked for.</summary>
    public MailMessage SignInMessage(string email, string token)
    {
        var link = _options.Link($"/account/verify/{token}");
        var minutes = (int)SignInToken.Lifetime.TotalMinutes;

        var text = $"""
            Someone asked to sign in to TicketMiser with this address.

            To sign in, open this link within {minutes} minutes. It works once:
            {link}

            If you did not ask for this, ignore this email. Nobody is signed in unless the link is opened, and no account exists for this address until it is.
            """;

        return new MailMessage(email, "Your TicketMiser sign-in link", text, null, new Dictionary<string, string>());
    }
}
