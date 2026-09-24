using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability;
using TicketMiser.Reliability.Accounts;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// A signed-in fan stops watching. Only that account's own watch is switched off — another
/// account's and the operator's stay on — and the alert email follows the watch: the
/// subscription the watch made is unsubscribed, while one the address had confirmed on the
/// record page by itself before signing in stays, because it was asked for separately. The
/// email a watcher gets says it comes from the account; the one a record-page subscriber gets
/// says it was asked for on the record page.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StopWatchingTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 16, 0, 0, TimeSpan.Zero);

    private static AccountService Accounts(TicketMiserDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example" });
        return new AccountService(db, new RecordingNotifier(), options, new FakeClock(Now), NullLogger<AccountService>.Instance);
    }

    private static (AlertDeliveryService Delivery, RecordingNotifier Notifier) Delivery(TicketMiserDbContext db)
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(Now);
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example" });
        var subscriptions = new SubscriptionService(db, notifier, options, clock, NullLogger<SubscriptionService>.Instance);
        return (new AlertDeliveryService(db, notifier, subscriptions, options, clock, NullLogger<AlertDeliveryService>.Instance), notifier);
    }

    private sealed record Seeded(Event Event, Source Source, Account Fan, Account Other);

    private static async Task<Seeded> SeedAsync(TicketMiserDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = new Source { Key = $"ticketmaster-inventory-{suffix}", Name = "Ticketmaster Inventory Status", Kind = SourceKind.Primary };
        var venue = new Venue { Name = $"Ryman Auditorium {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        var fan = new Account { Email = $"fan-{suffix}@example.com", CreatedAt = Now.AddDays(-1) };
        var other = new Account { Email = $"other-{suffix}@example.com", CreatedAt = Now.AddDays(-1) };
        db.AddRange(source, venue, fan, other);
        await db.SaveChangesAsync();

        var evt = new Event { Name = $"Stop Tour {suffix}", Slug = $"stop-tour-{suffix}", VenueId = venue.Id, StartsAt = Now.AddDays(30), OnSaleAt = Now.AddHours(-3) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        return new Seeded(evt, source, fan, other);
    }

    private static async Task OpenReappearedAlertAsync(TicketMiserDbContext db, Seeded seeded)
    {
        var run = new IngestionRun { SourceId = seeded.Source.Id, JobKey = "test", StartedAt = Now.AddHours(-3), Status = RunStatus.Success };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        db.OnSaleTicks.AddRange(
            new OnSaleTick { EventId = seeded.Event.Id, SourceId = seeded.Source.Id, IngestionRunId = run.Id, ObservedAt = Now.AddHours(-2), MinutesFromOnSale = 60, PrimaryStatus = InventoryStatus.NotAvailable },
            new OnSaleTick { EventId = seeded.Event.Id, SourceId = seeded.Source.Id, IngestionRunId = run.Id, ObservedAt = Now.AddMinutes(-40), MinutesFromOnSale = 140, PrimaryStatus = InventoryStatus.Available });
        db.Alerts.Add(new Alert
        {
            RuleKey = AlertRules.PrimaryReappeared,
            SourceId = seeded.Source.Id,
            EventId = seeded.Event.Id,
            Severity = AlertSeverity.Warn,
            Message = "test",
            TriggeredAt = Now.AddMinutes(-35)
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Stopping_switches_off_only_the_account_s_own_watch_and_the_email_that_came_with_it()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db);
        var accounts = Accounts(db);

        await accounts.WatchAsync(seeded.Fan.Id, seeded.Event.Id);
        await accounts.WatchAsync(seeded.Other.Id, seeded.Event.Id);
        db.Watches.Add(new Watch { EventId = seeded.Event.Id, OwnerId = null, CreatedAt = Now });
        await db.SaveChangesAsync();

        var result = await accounts.UnwatchAsync(seeded.Fan.Id, seeded.Event.Id);
        Assert.Equal(seeded.Event.Slug, result!.Slug);

        var watches = await db.Watches.AsNoTracking().Where(w => w.EventId == seeded.Event.Id).ToListAsync();
        Assert.False(watches.Single(w => w.OwnerId == seeded.Fan.Id).Enabled);
        Assert.True(watches.Single(w => w.OwnerId == seeded.Other.Id).Enabled);
        Assert.True(watches.Single(w => w.OwnerId == null).Enabled);

        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.EventId == seeded.Event.Id).ToListAsync();
        Assert.False(subs.Single(s => s.Email == seeded.Fan.Email).IsActive);
        Assert.True(subs.Single(s => s.Email == seeded.Other.Email).IsActive);

        // Stopping again is harmless, and an unknown event or account is a miss.
        Assert.NotNull(await accounts.UnwatchAsync(seeded.Fan.Id, seeded.Event.Id));
        Assert.Null(await accounts.UnwatchAsync(seeded.Fan.Id, int.MaxValue));
        Assert.Null(await accounts.UnwatchAsync(int.MaxValue, seeded.Event.Id));

        // Nothing is sent to the address that stopped: its row is unsubscribed, and the delivery
        // pass does not make it a new one for a watch that is off.
        await OpenReappearedAlertAsync(db, seeded);
        var (delivery, notifier) = Delivery(db);
        await delivery.DeliverAsync();

        Assert.Empty(notifier.To(seeded.Fan.Email));
        Assert.Single(notifier.To(seeded.Other.Email));
    }

    [Fact]
    public async Task A_record_page_subscription_confirmed_before_sign_in_outlives_the_watch()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db);
        var accounts = Accounts(db);

        // Double opt-in on the record page: a confirmation was sent and its link followed.
        db.Subscriptions.Add(new Subscription
        {
            Email = seeded.Fan.Email,
            EventId = seeded.Event.Id,
            CreatedAt = Now.AddDays(-3),
            ConfirmationSentAt = Now.AddDays(-3),
            ConfirmedAt = Now.AddDays(-3),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken()
        });
        await db.SaveChangesAsync();

        // Signing in claims it as a watch; the fan then stops watching.
        Assert.Equal(1, await accounts.ClaimSubscriptionsAsync(seeded.Fan));
        await accounts.UnwatchAsync(seeded.Fan.Id, seeded.Event.Id);

        Assert.False((await db.Watches.AsNoTracking().SingleAsync(w => w.EventId == seeded.Event.Id)).Enabled);
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == seeded.Event.Id);
        Assert.True(sub.IsActive);

        // It still arrives, worded as the record-page email it is, with its one-click unsubscribe.
        await OpenReappearedAlertAsync(db, seeded);
        var (delivery, notifier) = Delivery(db);
        await delivery.DeliverAsync();

        var message = Assert.Single(notifier.To(seeded.Fan.Email));
        Assert.Contains("You asked for this email on the record page.", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watching_again_after_stopping_brings_the_email_back_and_says_it_comes_from_the_account()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db);
        var accounts = Accounts(db);

        await accounts.WatchAsync(seeded.Fan.Id, seeded.Event.Id);
        await accounts.UnwatchAsync(seeded.Fan.Id, seeded.Event.Id);
        await accounts.WatchAsync(seeded.Fan.Id, seeded.Event.Id);

        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == seeded.Event.Id);
        Assert.True(sub.IsActive);
        Assert.True(AlertSubscriptions.MadeByWatch(sub));

        await OpenReappearedAlertAsync(db, seeded);
        var (delivery, notifier) = Delivery(db);
        await delivery.DeliverAsync();

        var message = Assert.Single(notifier.To(seeded.Fan.Email));
        Assert.DoesNotContain("record page", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("you watch this event from your TicketMiser account", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://tix.example/account", message.TextBody, StringComparison.Ordinal);
        Assert.Equal($"<https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}>", message.Headers["List-Unsubscribe"]);
    }

    [Fact]
    public async Task A_record_page_subscription_that_watch_activated_follows_the_watch()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db);
        var accounts = Accounts(db);

        // Asked on the record page, then unsubscribed; pressing Watch subscribes it again, so
        // stopping the watch stops it again rather than leaving it on.
        db.Subscriptions.Add(new Subscription
        {
            Email = seeded.Fan.Email,
            EventId = seeded.Event.Id,
            CreatedAt = Now.AddDays(-3),
            ConfirmationSentAt = Now.AddDays(-3),
            ConfirmedAt = Now.AddDays(-3),
            UnsubscribedAt = Now.AddDays(-2),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken()
        });
        await db.SaveChangesAsync();

        await accounts.WatchAsync(seeded.Fan.Id, seeded.Event.Id);
        Assert.True((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == seeded.Event.Id)).IsActive);

        await accounts.UnwatchAsync(seeded.Fan.Id, seeded.Event.Id);
        Assert.False((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == seeded.Event.Id)).IsActive);
    }
}
