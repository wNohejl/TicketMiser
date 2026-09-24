using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Web.Reports;

public enum ReportSendOutcome
{
    /// <summary>The pass ran; <see cref="ReportSendResult.Sent"/> says how many went out.</summary>
    Ran,

    /// <summary>The month is not a published report: nothing was read from the list, nothing sent.</summary>
    NotPublished,

    /// <summary>Another process holds the send lock: nothing sent by this one.</summary>
    Locked
}

/// <param name="Sent">Emails sent and recorded by this pass.</param>
/// <param name="Failed">Sends the notifier refused; no row, so the next run tries them again.</param>
/// <param name="AlreadySent">Confirmed subscribers who already had this month's row, by an earlier pass or a concurrent one.</param>
public sealed record ReportSendResult(ReportSendOutcome Outcome, string Month, int Sent = 0, int Failed = 0, int AlreadySent = 0);

/// <summary>
/// Mails a published month's report to the report list, once. The operator runs it after the
/// deploy that publishes the report (<c>send-report</c>, see <see cref="SendReportCommand"/>);
/// nothing schedules it.
///
/// <para>
/// Only <see cref="ReportSubscription"/> rows are read, never <see cref="Subscription"/>:
/// an address given for an event's alert was given for that alert only (legal-guidelines
/// rule 8). Idempotent by the <see cref="ReportDelivery"/> row, as alert delivery is by its
/// own: one email is sent and <em>then</em> the row is inserted, so a failed send is retried
/// by the next run and a successful one is never repeated. A session advisory lock keeps two
/// runs from sending at once; the unique index on (ReportSubscriptionId, Month) is the
/// backstop, and a violation there is logged rather than thrown.
/// </para>
/// </summary>
public sealed class ReportMailingService(
    TicketMiserDbContext db,
    INotifier notifier,
    IReportLibrary reports,
    ReportSubscriptionService subscriptions,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<ReportMailingService> logger)
{
    /// <summary>An arbitrary constant naming this pass to pg_try_advisory_lock; not the alert pass's.</summary>
    public const long AdvisoryLockKey = 0x2E9087_DE11;

    private readonly NotificationOptions _options = options.Value;

    public async Task<ReportSendResult> SendAsync(string month, CancellationToken ct = default)
    {
        if (reports.Get(month) is not { } report)
            return new ReportSendResult(ReportSendOutcome.NotPublished, month);

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var locked = await db.Database
                .SqlQueryRaw<bool>("SELECT pg_try_advisory_lock({0}) AS \"Value\"", AdvisoryLockKey)
                .SingleAsync(ct);

            if (!locked)
            {
                logger.LogInformation("Report send for {Month} skipped: another process holds the send lock", month);
                return new ReportSendResult(ReportSendOutcome.Locked, month);
            }

            try
            {
                return await SendLockedAsync(report, ct);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", [AdvisoryLockKey], CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<ReportSendResult> SendLockedAsync(ReportDocument report, CancellationToken ct)
    {
        var month = report.Month;

        var confirmed = db.ReportSubscriptions.AsNoTracking()
            .Where(s => s.ConfirmedAt != null && s.UnsubscribedAt == null);

        var alreadySent = await confirmed.CountAsync(s => db.ReportDeliveries.Any(d => d.ReportSubscriptionId == s.Id && d.Month == month), ct);

        var owed = await confirmed
            .Where(s => !db.ReportDeliveries.Any(d => d.ReportSubscriptionId == s.Id && d.Month == month))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        int sent = 0, failed = 0;

        foreach (var sub in owed)
        {
            string? providerId;
            try
            {
                providerId = await notifier.SendAsync(ReportMessage(report, sub), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No row, so the next run tries this one again.
                logger.LogError(ex, "Report {Month} to report subscription {Id} not sent", month, sub.Id);
                failed++;
                continue;
            }

            var delivery = new ReportDelivery
            {
                ReportSubscriptionId = sub.Id,
                Month = month,
                SentAt = clock.GetUtcNow(),
                ProviderMessageId = providerId
            };
            db.ReportDeliveries.Add(delivery);

            try
            {
                await db.SaveChangesAsync(ct);
                sent++;
            }
            catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
            {
                db.Entry(delivery).State = EntityState.Detached;
                alreadySent++;
                logger.LogWarning("Report {Month} to report subscription {Id} was already recorded by another run", month, sub.Id);
            }
        }

        logger.LogInformation("Report {Month}: {Sent} sent, {Failed} failed, {AlreadySent} already sent", month, sent, failed, alreadySent);
        return new ReportSendResult(ReportSendOutcome.Ran, month, sent, failed, alreadySent);
    }

    /// <summary>
    /// The report email: plain text, the report's summary paragraph, the link to the whole
    /// report, and how to stop. The tables stay on the page, where every number links to its
    /// source.
    /// </summary>
    public MailMessage ReportMessage(ReportDocument report, ReportSubscription sub)
    {
        var page = _options.Link($"/reports/{report.Month}");
        var unsubscribe = _options.Link(ReportSubscriptionService.UnsubscribePath(sub));

        var text = new StringBuilder()
            .Append(report.Title).Append("\n\n");

        if (report.Summary.Length > 0)
            text.Append(report.Summary).Append("\n\n");

        text.Append("The whole report, with its tables and how each number was measured:\n").Append(page).Append("\n\n")
            .Append("You asked for the monthly report at ").Append(_options.Link("/reports"))
            .Append(". The address is used only for this report.\n")
            .Append("Unsubscribe with one click:\n").Append(unsubscribe).Append('\n');

        return new MailMessage(sub.Email, report.Title, text.ToString(), null, subscriptions.UnsubscribeHeaders(sub));
    }
}
