using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Reliability.Notifications;

/// <summary>What a report-list subscribe did. The caller answers the same way for both accepted cases.</summary>
public enum ReportSubscribeOutcome
{
    /// <summary>Waiting on a confirmation (sent now, or within the hour), or already confirmed.</summary>
    Accepted,

    /// <summary>Not an address: nothing stored, nothing sent.</summary>
    InvalidAddress
}

/// <summary>
/// Double opt-in for the monthly Nashville report: its own list, apart from the per-event
/// alerts (<see cref="SubscriptionService"/>), because rule 8 of docs/legal-guidelines.md
/// holds an address to the purpose it was given for. An address subscribed to an event's
/// alert is never on this list unless it subscribes here too, and confirms.
///
/// <para>
/// The same shape as the alert list, with its helpers reused: the address is normalised by
/// <see cref="SubscriptionService.NormaliseEmail"/>, tokens are
/// <see cref="SubscriptionService.NewToken"/>, a repeat subscribe reuses the row and resends
/// the confirmation at most once an hour, and every email carries the RFC 8058 headers.
/// </para>
/// </summary>
public class ReportSubscriptionService(
    TicketMiserDbContext db,
    INotifier notifier,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<ReportSubscriptionService> logger)
{
    private readonly NotificationOptions _options = options.Value;

    public async Task<ReportSubscribeOutcome> SubscribeAsync(string? emailInput, CancellationToken ct = default)
    {
        if (SubscriptionService.NormaliseEmail(emailInput) is not { } email)
            return ReportSubscribeOutcome.InvalidAddress;

        var now = clock.GetUtcNow();
        var sub = await db.ReportSubscriptions.FirstOrDefaultAsync(s => s.Email == email, ct);

        if (sub is null)
        {
            sub = new ReportSubscription
            {
                Email = email,
                CreatedAt = now,
                ConfirmToken = SubscriptionService.NewToken(),
                UnsubscribeToken = SubscriptionService.NewToken()
            };
            db.ReportSubscriptions.Add(sub);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
            {
                // Two submits of the same address at once: the other one made the row.
                db.Entry(sub).State = EntityState.Detached;
                sub = await db.ReportSubscriptions.FirstAsync(s => s.Email == email, ct);
            }
        }
        else if (sub.IsActive)
        {
            // Already confirmed: nothing to send, and the response must not say so.
            return ReportSubscribeOutcome.Accepted;
        }
        else if (sub.UnsubscribedAt is not null)
        {
            // Back after unsubscribing: a fresh opt-in, with fresh secrets, so an old link does nothing.
            sub.UnsubscribedAt = null;
            sub.ConfirmedAt = null;
            sub.ConfirmationSentAt = null;
            sub.ConfirmToken = SubscriptionService.NewToken();
            sub.UnsubscribeToken = SubscriptionService.NewToken();
            await db.SaveChangesAsync(ct);
        }

        if (sub.ConfirmationSentAt is { } sent && now - sent < _options.ConfirmationResendAfter)
        {
            logger.LogInformation("Report-list confirmation for {Id} not resent: last sent {Sent:o}", sub.Id, sent);
            return ReportSubscribeOutcome.Accepted;
        }

        try
        {
            await notifier.SendAsync(ConfirmationMessage(sub), ct);
            sub.ConfirmationSentAt = now;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row stays unconfirmed with no send recorded, so the next submit tries again.
            logger.LogError(ex, "Report-list confirmation email for {Id} failed", sub.Id);
        }

        return ReportSubscribeOutcome.Accepted;
    }

    /// <summary>
    /// Whether a confirmation link would confirm something, for the GET that draws its button.
    /// Reads only: a link scanner that opens the link confirms nobody.
    /// </summary>
    public Task<bool> IsConfirmableAsync(string token, CancellationToken ct = default)
        => db.ReportSubscriptions.AnyAsync(s => s.ConfirmToken == token && s.UnsubscribedAt == null, ct);

    /// <summary>
    /// Confirms by token, from the button's POST. False for a token that matches nothing or an
    /// address that has since unsubscribed. An account with the same address is linked, so the
    /// account's deletion takes the row with it.
    /// </summary>
    public async Task<bool> ConfirmAsync(string token, CancellationToken ct = default)
    {
        var sub = await db.ReportSubscriptions.FirstOrDefaultAsync(s => s.ConfirmToken == token && s.UnsubscribedAt == null, ct);
        if (sub is null)
            return false;

        // Idempotent: a second press is still a confirmation, dated the first.
        sub.ConfirmedAt ??= clock.GetUtcNow();
        sub.AccountId ??= await db.Accounts.Where(a => a.Email == sub.Email).Select(a => (int?)a.Id).FirstOrDefaultAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Unsubscribes by token. Returns whether a row was found, which the caller must not show.</summary>
    public async Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default)
    {
        var sub = await db.ReportSubscriptions.FirstOrDefaultAsync(s => s.UnsubscribeToken == token, ct);
        if (sub is null)
            return false;

        sub.UnsubscribedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The path whose GET shows the unsubscribe button and whose POST unsubscribes.</summary>
    public static string UnsubscribePath(ReportSubscription sub) => $"/r/unsubscribe/{sub.UnsubscribeToken}";

    /// <summary>RFC 2369 and RFC 8058, pointing at the report list's own unsubscribe.</summary>
    public IReadOnlyDictionary<string, string> UnsubscribeHeaders(ReportSubscription sub)
        => SubscriptionService.UnsubscribeHeaders(_options, UnsubscribePath(sub));

    /// <summary>The confirmation email: what was asked for, that it may not have been this reader, and how to stop it.</summary>
    public MailMessage ConfirmationMessage(ReportSubscription sub)
    {
        var confirm = _options.Link($"/r/confirm/{sub.ConfirmToken}");
        var unsubscribe = _options.Link(UnsubscribePath(sub));
        var reports = _options.Link("/reports");

        var text = $"""
            Someone asked for this address to get the monthly Nashville on-sale report by email: one email a month, when a report is published.

            To confirm, open this link and press the button:
            {confirm}

            If you did not ask for this, ignore this email. Nothing more will be sent unless the link above is opened and confirmed.

            The address is used only for the monthly report. Every email has a one-click unsubscribe:
            {unsubscribe}

            Every published report:
            {reports}
            """;

        return new MailMessage(sub.Email, "Confirm: the monthly Nashville on-sale report", text, null, UnsubscribeHeaders(sub));
    }
}
