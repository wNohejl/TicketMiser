using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Reliability.Notifications;

public enum SubscribeOutcome
{
    /// <summary>The address is waiting on a confirmation (sent now, or within the last hour), or is already confirmed. The caller says the same thing either way.</summary>
    Accepted,

    /// <summary>Not an address: nothing stored, nothing sent.</summary>
    InvalidAddress,

    /// <summary>No event at that address.</summary>
    EventNotFound
}

/// <summary>What a subscribe did. <see cref="Slug"/> is the event's own slug, for the redirect.</summary>
public record SubscribeResult(SubscribeOutcome Outcome, string? Slug = null);

/// <summary>A confirmed subscription, for the confirmation page: the event's name and its address.</summary>
public record ConfirmedSubscription(string EventName, string? Slug);

/// <summary>
/// Double opt-in for "tell me if face value comes back", per event.
///
/// <para>
/// Subscribing stores an unconfirmed row and sends one confirmation email; nothing else is
/// ever sent to an address that has not followed that link. A repeat subscribe reuses the row
/// and its tokens and resends the confirmation at most once an hour, so the form cannot be
/// used to flood someone else's inbox. The caller answers every subscribe the same way,
/// whatever this finds, so the form does not reveal who is subscribed.
/// </para>
/// </summary>
public class SubscriptionService(
    TicketMiserDbContext db,
    INotifier notifier,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<SubscriptionService> logger)
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>Trimmed, lower-cased, and a bare address — or null. No display names, no control characters.</summary>
    public static string? NormaliseEmail(string? input)
    {
        var email = input?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(email) || email.Length > Subscription.EmailMaxLength || email.Any(char.IsControl))
            return null;

        return MailAddress.TryCreate(email, out var parsed)
               && parsed.Address == email
               && string.IsNullOrEmpty(parsed.DisplayName)
               && parsed.Host.Contains('.', StringComparison.Ordinal)
            ? email
            : null;
    }

    /// <summary>A 32-byte random secret, base64url without padding: 43 characters, safe in a path.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public async Task<SubscribeResult> SubscribeAsync(string slug, string? emailInput, CancellationToken ct = default)
    {
        var evt = await db.Events.Include(e => e.Venue).FirstOrDefaultAsync(e => e.Slug == slug, ct);
        if (evt is null)
            return new SubscribeResult(SubscribeOutcome.EventNotFound);

        if (NormaliseEmail(emailInput) is not { } email)
            return new SubscribeResult(SubscribeOutcome.InvalidAddress, evt.Slug);

        var now = clock.GetUtcNow();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Email == email && s.EventId == evt.Id, ct);

        if (sub is null)
        {
            sub = new Subscription
            {
                Email = email,
                EventId = evt.Id,
                CreatedAt = now,
                ConfirmToken = NewToken(),
                UnsubscribeToken = NewToken()
            };
            db.Subscriptions.Add(sub);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Two submits of the same address at once: the other one made the row.
                db.Entry(sub).State = EntityState.Detached;
                sub = await db.Subscriptions.FirstAsync(s => s.Email == email && s.EventId == evt.Id, ct);
            }
        }
        else if (sub.IsActive)
        {
            // Already confirmed: nothing to send, and the response must not say so.
            return new SubscribeResult(SubscribeOutcome.Accepted, evt.Slug);
        }
        else if (sub.UnsubscribedAt is not null)
        {
            // Back after unsubscribing: a fresh opt-in, with fresh secrets, so an old link does nothing.
            sub.UnsubscribedAt = null;
            sub.ConfirmedAt = null;
            sub.ConfirmationSentAt = null;
            sub.ConfirmToken = NewToken();
            sub.UnsubscribeToken = NewToken();
            await db.SaveChangesAsync(ct);
        }

        if (sub.ConfirmationSentAt is { } sent && now - sent < _options.ConfirmationResendAfter)
        {
            logger.LogInformation("Confirmation for subscription {Id} not resent: last sent {Sent:o}", sub.Id, sent);
            return new SubscribeResult(SubscribeOutcome.Accepted, evt.Slug);
        }

        try
        {
            await notifier.SendAsync(ConfirmationMessage(sub, evt), ct);
            sub.ConfirmationSentAt = now;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row stays unconfirmed with no send recorded, so the next submit tries again.
            logger.LogError(ex, "Confirmation email for subscription {Id} failed", sub.Id);
        }

        return new SubscribeResult(SubscribeOutcome.Accepted, evt.Slug);
    }

    /// <summary>Confirms by token. Null for a token that matches nothing or an address that has since unsubscribed.</summary>
    public async Task<ConfirmedSubscription?> ConfirmAsync(string token, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.ConfirmToken == token && s.UnsubscribedAt == null, ct);

        if (sub is null)
            return null;

        // Idempotent: a second click on the same link is still a confirmation, dated the first.
        sub.ConfirmedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new ConfirmedSubscription(sub.Event!.Name, sub.Event.Slug);
    }

    /// <summary>
    /// Unsubscribes by token. Returns whether a subscription was found, which the caller must
    /// not show: the page is the same for an unknown token.
    /// </summary>
    public async Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UnsubscribeToken == token, ct);
        if (sub is null)
            return false;

        sub.UnsubscribedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The confirmation email. Says what was asked for, by whom it may not have been, and how to stop it.</summary>
    public MailMessage ConfirmationMessage(Subscription sub, Event evt)
    {
        var where = evt.Venue is { } v ? $" at {v.Name}" : string.Empty;
        var confirm = _options.Link($"/s/confirm/{sub.ConfirmToken}");
        var unsubscribe = _options.Link($"/s/unsubscribe/{sub.UnsubscribeToken}");
        var record = _options.Link($"/e/{evt.Slug}");

        var text = $"""
            Someone asked for this address to get one email if Ticketmaster shows face-value tickets for {evt.Name}{where} as available again after they were not available.

            To confirm, open this link:
            {confirm}

            If you did not ask for this, ignore this email. Nothing more will be sent unless the link above is opened.

            The address is used only for this alert. Every email has a one-click unsubscribe:
            {unsubscribe}

            The on-sale record for this event:
            {record}
            """;

        return new MailMessage(sub.Email, $"Confirm: face-value alert for {evt.Name}", text, null, UnsubscribeHeaders(sub));
    }

    /// <summary>RFC 2369 and RFC 8058: a mail client's own unsubscribe button posts to this, one click, no page.</summary>
    public IReadOnlyDictionary<string, string> UnsubscribeHeaders(Subscription sub) => new Dictionary<string, string>
    {
        ["List-Unsubscribe"] = $"<{_options.Link($"/s/unsubscribe/{sub.UnsubscribeToken}")}>",
        ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
    };

    internal static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
