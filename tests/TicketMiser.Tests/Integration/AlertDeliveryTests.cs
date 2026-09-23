using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// One open <c>primary_reappeared</c> alert and one confirmed subscriber become exactly one
/// email and one <see cref="AlertDelivery"/> row, and a second evaluation sends nothing more.
/// Everyone else — unconfirmed, unsubscribed, or subscribed to a different rule's event — gets
/// nothing. The database is shared by the collection, so every assertion counts messages to
/// this test's own address.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AlertDeliveryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);

    private static readonly NotificationOptions Options = new() { PublicBaseUrl = "https://tix.example" };

    private sealed record Seeded(Event Event, Source Source, string Suffix);

    private static (AlertDeliveryService Delivery, RecordingNotifier Notifier) Build(TicketMiserDbContext db, RecordingNotifier? notifier = null)
    {
        notifier ??= new RecordingNotifier();
        var clock = new FakeClock(Now);
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var subscriptions = new SubscriptionService(db, notifier, options, clock, NullLogger<SubscriptionService>.Instance);

        return (new AlertDeliveryService(db, notifier, subscriptions, options, clock, NullLogger<AlertDeliveryService>.Instance), notifier);
    }

    private static async Task<Seeded> SeedEventAsync(TicketMiserDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = new Source { Key = $"ticketmaster-inventory-{suffix}", Name = "Ticketmaster Inventory Status", Kind = SourceKind.Primary };
        var venue = new Venue { Name = $"Bridgestone Arena {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        db.AddRange(source, venue);
        await db.SaveChangesAsync();

        var evt = new Event
        {
            Name = $"Example Tour {suffix}",
            Slug = $"example-tour-{suffix}",
            VenueId = venue.Id,
            StartsAt = Now.AddDays(30),
            OnSaleAt = Now.AddHours(-3),
            ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = $"tm-{suffix}" }
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        // The sellout and the reappearance the rule fired on: 15:20 UTC is 10:20 in Nashville.
        var run = new IngestionRun { SourceId = source.Id, JobKey = "test", StartedAt = Now.AddHours(-3), Status = RunStatus.Success };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        db.OnSaleTicks.AddRange(
            new OnSaleTick { EventId = evt.Id, SourceId = source.Id, IngestionRunId = run.Id, ObservedAt = Now.AddHours(-2), MinutesFromOnSale = 60, PrimaryStatus = InventoryStatus.NotAvailable },
            new OnSaleTick { EventId = evt.Id, SourceId = source.Id, IngestionRunId = run.Id, ObservedAt = Now.AddMinutes(-40), MinutesFromOnSale = 140, PrimaryStatus = InventoryStatus.Available });
        await db.SaveChangesAsync();

        return new Seeded(evt, source, suffix);
    }

    private static async Task<Alert> OpenAlertAsync(TicketMiserDbContext db, Seeded seeded, string rule = AlertRules.PrimaryReappeared)
    {
        var alert = new Alert
        {
            RuleKey = rule,
            SourceId = seeded.Source.Id,
            EventId = seeded.Event.Id,
            Severity = AlertSeverity.Warn,
            Message = "test",
            TriggeredAt = Now.AddMinutes(-35)
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert;
    }

    private static async Task<Subscription> SubscribeAsync(TicketMiserDbContext db, Seeded seeded, string who, bool confirmed = true, bool unsubscribed = false)
    {
        var sub = new Subscription
        {
            Email = $"{who}-{seeded.Suffix}@example.com",
            EventId = seeded.Event.Id,
            CreatedAt = Now.AddDays(-1),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken(),
            ConfirmationSentAt = Now.AddDays(-1),
            ConfirmedAt = confirmed ? Now.AddDays(-1) : null,
            UnsubscribedAt = unsubscribed ? Now.AddHours(-1) : null
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub;
    }

    [Fact]
    public async Task One_alert_and_one_confirmed_subscriber_is_one_send_and_one_row_and_a_second_run_sends_nothing()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        var alert = await OpenAlertAsync(db, seeded);
        var sub = await SubscribeAsync(db, seeded, "fan");
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        var sent = Assert.Single(notifier.To(sub.Email));
        var row = await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.SubscriptionId == sub.Id);
        Assert.Equal(alert.Id, row.AlertId);
        Assert.NotNull(row.ProviderMessageId);

        await delivery.DeliverAsync();

        Assert.Single(notifier.To(sub.Email));
        Assert.Equal(1, await db.AlertDeliveries.CountAsync(d => d.SubscriptionId == sub.Id));

        // A second process, with its own context, finds the row and sends nothing either.
        await using var other = fixture.CreateContext();
        var (again, otherNotifier) = Build(other);
        await again.DeliverAsync();
        Assert.Empty(otherNotifier.To(sub.Email));

        Assert.Contains(seeded.Event.Name, sent.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_email_says_what_the_source_reported_and_when_links_the_record_and_the_source_and_carries_one_click_unsubscribe()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        await OpenAlertAsync(db, seeded);
        var sub = await SubscribeAsync(db, seeded, "reader");
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        var message = Assert.Single(notifier.To(sub.Email));

        // Rule 9: what the source said, when, in the room's zone; no conclusion.
        Assert.Contains(
            $"Ticketmaster's availability for {seeded.Event.Name} at Bridgestone Arena {seeded.Suffix} changed from not available to available at Fri 2 Oct 2026 10:20 America/Chicago.",
            message.TextBody, StringComparison.Ordinal);
        Assert.Contains("This is what the source reported; check the event page before buying.", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("violat", message.TextBody, StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"https://tix.example/e/{seeded.Event.Slug}", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://www.ticketmaster.com/event/tm-{seeded.Suffix}", message.TextBody, StringComparison.Ordinal);

        var unsubscribe = $"https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}";
        Assert.Equal($"<{unsubscribe}>", message.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", message.Headers["List-Unsubscribe-Post"]);
        Assert.Contains(unsubscribe, message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfirmed_and_unsubscribed_addresses_get_nothing()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        await OpenAlertAsync(db, seeded);
        var pending = await SubscribeAsync(db, seeded, "pending", confirmed: false);
        var gone = await SubscribeAsync(db, seeded, "gone", confirmed: true, unsubscribed: true);
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        Assert.Empty(notifier.To(pending.Email));
        Assert.Empty(notifier.To(gone.Email));
        Assert.Equal(0, await db.AlertDeliveries.CountAsync(d => d.SubscriptionId == pending.Id || d.SubscriptionId == gone.Id));
    }

    [Fact]
    public async Task An_alert_for_another_rule_or_a_resolved_one_is_not_delivered()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        await OpenAlertAsync(db, seeded, AlertRules.PriceDrop);
        var resolved = await OpenAlertAsync(db, seeded);
        resolved.ResolvedAt = Now.AddMinutes(-5);
        await db.SaveChangesAsync();
        var sub = await SubscribeAsync(db, seeded, "other-rule");
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        Assert.Empty(notifier.To(sub.Email));
    }

    [Fact]
    public async Task A_failed_send_writes_no_row_and_is_retried_on_the_next_run()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        await OpenAlertAsync(db, seeded);
        var sub = await SubscribeAsync(db, seeded, "retry");
        var notifier = new RecordingNotifier { FailWith = new HttpRequestException("provider down") };
        var (delivery, _) = Build(db, notifier);

        await delivery.DeliverAsync();
        Assert.Equal(0, await db.AlertDeliveries.CountAsync(d => d.SubscriptionId == sub.Id));

        notifier.FailWith = null;
        await delivery.DeliverAsync();

        Assert.Single(notifier.To(sub.Email));
        Assert.Equal(1, await db.AlertDeliveries.CountAsync(d => d.SubscriptionId == sub.Id));
    }

    [Fact]
    public async Task A_run_that_finds_another_process_delivering_sends_nothing()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedEventAsync(db);
        await OpenAlertAsync(db, seeded);
        var sub = await SubscribeAsync(db, seeded, "locked");

        // Another process holds the delivery lock for the whole of this run.
        await using var holder = fixture.CreateContext();
        await holder.Database.OpenConnectionAsync();
        var key = 0x71C4E7_DE11L;
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", key);

        try
        {
            var (delivery, notifier) = Build(db);
            Assert.Equal(0, await delivery.DeliverAsync());
            Assert.Empty(notifier.To(sub.Email));
        }
        finally
        {
            await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", key);
            await holder.Database.CloseConnectionAsync();
        }
    }
}
