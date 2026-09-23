using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Reliability.Notifications;

/// <summary>
/// Turns open alerts into email for the people who asked. <c>primary_reappeared</c> is the
/// first rule delivered, and for now the only one: it is the alert a fan subscribes to on the
/// public record page.
///
/// <para>
/// Idempotent by the <see cref="AlertDelivery"/> row. For each open alert and each active
/// subscription on its event with no row yet, one email is sent and <em>then</em> the row is
/// inserted, so a failed send leaves no row and is retried next cycle, and a successful one is
/// never repeated. Two processes (the web host and the worker can both run the evaluator) are
/// kept from sending the same email at once by a session advisory lock around the whole pass;
/// the unique index on (AlertId, SubscriptionId) stays underneath as the backstop, and a
/// violation there is logged rather than treated as a failure.
/// </para>
///
/// <para>
/// The wording is bound by rule 9 of docs/legal-guidelines.md: the email reports what the
/// source said and when, and draws no conclusion about anyone.
/// </para>
/// </summary>
public class AlertDeliveryService(
    TicketMiserDbContext db,
    INotifier notifier,
    SubscriptionService subscriptions,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<AlertDeliveryService> logger)
{
    /// <summary>The rules whose alerts are emailed to subscribers.</summary>
    public static readonly IReadOnlySet<string> DeliveredRules = new HashSet<string>(StringComparer.Ordinal)
    {
        AlertRules.PrimaryReappeared
    };

    /// <summary>An arbitrary constant naming this pass to pg_try_advisory_lock.</summary>
    private const long AdvisoryLockKey = 0x71C4E7_DE11;

    private readonly NotificationOptions _options = options.Value;

    /// <summary>Sends what is owed and returns how many emails went out.</summary>
    public async Task<int> DeliverAsync(CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await TryLockAsync(ct))
            {
                logger.LogInformation("Alert delivery skipped: another process holds the delivery lock");
                return 0;
            }

            try
            {
                return await DeliverLockedAsync(ct);
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

    private async Task<bool> TryLockAsync(CancellationToken ct)
        => await db.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_lock({0}) AS \"Value\"", AdvisoryLockKey)
            .SingleAsync(ct);

    private async Task<int> DeliverLockedAsync(CancellationToken ct)
    {
        var rules = DeliveredRules.ToArray();

        var alerts = await db.Alerts
            .AsNoTracking()
            .Include(a => a.Source)
            .Include(a => a.Event).ThenInclude(e => e!.Venue)
            .Where(a => a.ResolvedAt == null && a.EventId != null && rules.Contains(a.RuleKey))
            .OrderBy(a => a.TriggeredAt)
            .ToListAsync(ct);

        var sent = 0;

        foreach (var alert in alerts)
        {
            // A signed-in fan's watch is a confirmed subscription for delivery; make sure its
            // row exists, so the read below finds the account's address exactly once.
            await AlertSubscriptions.EnsureForOwnedWatchesAsync(db, alert.EventId!.Value, clock.GetUtcNow(), ct);

            var owed = await db.Subscriptions
                .AsNoTracking()
                .Where(s => s.EventId == alert.EventId
                            && s.ConfirmedAt != null
                            && s.UnsubscribedAt == null
                            && !db.AlertDeliveries.Any(d => d.AlertId == alert.Id && d.SubscriptionId == s.Id))
                .OrderBy(s => s.Id)
                .ToListAsync(ct);

            if (owed.Count == 0)
                continue;

            var reappearedAt = await ReappearedAtAsync(alert, ct);

            foreach (var sub in owed)
            {
                string? providerId;
                try
                {
                    providerId = await notifier.SendAsync(AlertMessage(alert, sub, reappearedAt), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // No row, so the next evaluation tries this one again.
                    logger.LogError(ex, "Alert {AlertId} to subscription {SubscriptionId} not sent", alert.Id, sub.Id);
                    continue;
                }

                var delivery = new AlertDelivery
                {
                    AlertId = alert.Id,
                    SubscriptionId = sub.Id,
                    SentAt = clock.GetUtcNow(),
                    ProviderMessageId = providerId
                };
                db.AlertDeliveries.Add(delivery);

                try
                {
                    await db.SaveChangesAsync(ct);
                    sent++;
                }
                catch (DbUpdateException ex) when (SubscriptionService.IsUniqueViolation(ex))
                {
                    db.Entry(delivery).State = EntityState.Detached;
                    logger.LogWarning("Alert {AlertId} to subscription {SubscriptionId} was already recorded by another run",
                        alert.Id, sub.Id);
                }
            }
        }

        if (sent > 0)
            logger.LogInformation("Delivered {Count} alert emails", sent);

        return sent;
    }

    /// <summary>
    /// The minute the source reported primary available again: the first Available tick after
    /// the first NotAvailable one in the rule's week, as <see cref="AlertEngine"/> finds it.
    /// Falls back to when the alert opened, which is within one evaluation of it.
    /// </summary>
    private async Task<DateTimeOffset> ReappearedAtAsync(Alert alert, CancellationToken ct)
    {
        var since = alert.TriggeredAt.AddDays(-7);

        var ticks = await db.OnSaleTicks
            .AsNoTracking()
            .Where(t => t.EventId == alert.EventId && t.PrimaryStatus != null && t.ObservedAt >= since && t.ObservedAt <= alert.TriggeredAt)
            .OrderBy(t => t.ObservedAt)
            .Select(t => new { t.ObservedAt, t.PrimaryStatus })
            .ToListAsync(ct);

        var gone = ticks.FirstOrDefault(t => t.PrimaryStatus == InventoryStatus.NotAvailable);
        var back = gone is null
            ? null
            : ticks.FirstOrDefault(t => t.ObservedAt > gone.ObservedAt && t.PrimaryStatus == InventoryStatus.Available);

        return back?.ObservedAt ?? alert.TriggeredAt;
    }

    /// <summary>The alert email: what the source reported, when, where to check it, and how to stop.</summary>
    public MailMessage AlertMessage(Alert alert, Subscription sub, DateTimeOffset reappearedAt)
    {
        var evt = alert.Event!;
        var venue = evt.Venue?.Name ?? "the venue";
        var source = alert.Source;
        var sourceName = source is null || source.Key.StartsWith("ticketmaster", StringComparison.Ordinal)
            ? "Ticketmaster"
            : source.Name;

        var when = Local(reappearedAt, evt.Venue?.Timezone);
        var record = _options.Link($"/e/{evt.Slug ?? evt.Id.ToString(CultureInfo.InvariantCulture)}");
        var sourcePage = source is null ? null : SourceLink.For(source, evt);
        var unsubscribe = _options.Link($"/s/unsubscribe/{sub.UnsubscribeToken}");

        var text = new StringBuilder()
            .Append(sourceName).Append("'s availability for ").Append(evt.Name).Append(" at ").Append(venue)
            .Append(" changed from not available to available at ").Append(when)
            .Append(". This is what the source reported; check the event page before buying.\n\n")
            .Append("The on-sale record, minute by minute:\n").Append(record).Append("\n\n");

        if (sourcePage is not null)
            text.Append("The event on ").Append(sourceName).Append(":\n").Append(sourcePage).Append("\n\n");

        text.Append("You asked for this email on the record page. The address is used only for this alert.\n")
            .Append("Unsubscribe with one click:\n").Append(unsubscribe).Append('\n');

        return new MailMessage(
            sub.Email,
            $"{evt.Name}: {sourceName} shows tickets available again",
            text.ToString(),
            null,
            subscriptions.UnsubscribeHeaders(sub));
    }

    /// <summary>"Fri 18 Sep 2026 11:35 America/Chicago": the record page's format, in the room's zone.</summary>
    public static string Local(DateTimeOffset instant, string? zone)
    {
        if (zone is { Length: > 0 })
        {
            try
            {
                var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(zone));
                return $"{local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} {zone}";
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall through to UTC, which is at least true.
            }
        }

        return $"{instant.UtcDateTime.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} UTC";
    }
}
