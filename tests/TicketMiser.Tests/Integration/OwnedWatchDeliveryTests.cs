using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// A signed-in fan's watch counts as a confirmed subscription for the "face value is back"
/// email: it delivers one email per reappearance to the account's address, and one — not two —
/// when the same address also subscribed on the record page. And two owners on one event raise
/// one alert, so neither the alert nor the email is doubled by the union.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OwnedWatchDeliveryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);

    private static (AlertDeliveryService Delivery, RecordingNotifier Notifier) Build(TicketMiserDbContext db)
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(Now);
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example" });
        var subscriptions = new SubscriptionService(db, notifier, options, clock, NullLogger<SubscriptionService>.Instance);
        return (new AlertDeliveryService(db, notifier, subscriptions, options, clock, NullLogger<AlertDeliveryService>.Instance), notifier);
    }

    private sealed record Seeded(Event Event, Source Source, Account Account);

    private static async Task<Seeded> SeedAsync(TicketMiserDbContext db, DateTimeOffset now)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = new Source { Key = $"ticketmaster-inventory-{suffix}", Name = "Ticketmaster Inventory Status", Kind = SourceKind.Primary };
        var venue = new Venue { Name = $"Bridgestone Arena {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        var account = new Account { Email = $"owner-{suffix}@example.com", CreatedAt = now.AddDays(-1) };
        db.AddRange(source, venue, account);
        await db.SaveChangesAsync();

        var evt = new Event { Name = $"Owned Tour {suffix}", Slug = $"owned-tour-{suffix}", VenueId = venue.Id, StartsAt = now.AddDays(30), OnSaleAt = now.AddHours(-3) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var run = new IngestionRun { SourceId = source.Id, JobKey = "test", StartedAt = now.AddHours(-3), Status = RunStatus.Success };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        db.OnSaleTicks.AddRange(
            new OnSaleTick { EventId = evt.Id, SourceId = source.Id, IngestionRunId = run.Id, ObservedAt = now.AddHours(-2), MinutesFromOnSale = 60, PrimaryStatus = InventoryStatus.NotAvailable },
            new OnSaleTick { EventId = evt.Id, SourceId = source.Id, IngestionRunId = run.Id, ObservedAt = now.AddMinutes(-40), MinutesFromOnSale = 140, PrimaryStatus = InventoryStatus.Available });
        db.Watches.Add(new Watch { EventId = evt.Id, OwnerId = account.Id, CreatedAt = now.AddDays(-1) });
        await db.SaveChangesAsync();

        return new Seeded(evt, source, account);
    }

    private static async Task OpenAlertAsync(TicketMiserDbContext db, Seeded seeded)
    {
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
    public async Task An_owned_watch_delivers_one_email_per_reappearance_to_the_account()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db, Now);
        await OpenAlertAsync(db, seeded);
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();
        await delivery.DeliverAsync();

        var message = Assert.Single(notifier.To(seeded.Account.Email));
        Assert.Contains(seeded.Event.Name, message.Subject, StringComparison.Ordinal);

        // It went through a subscription row like any other, so it has a one-click unsubscribe.
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == seeded.Event.Id);
        Assert.Equal(seeded.Account.Id, sub.AccountId);
        Assert.Equal($"<https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}>", message.Headers["List-Unsubscribe"]);
        Assert.Equal(1, await db.AlertDeliveries.CountAsync(d => d.SubscriptionId == sub.Id));
    }

    [Fact]
    public async Task An_owned_watch_and_a_confirmed_subscription_for_the_same_address_are_one_email()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db, Now);

        db.Subscriptions.Add(new Subscription
        {
            Email = seeded.Account.Email,
            EventId = seeded.Event.Id,
            CreatedAt = Now.AddDays(-2),
            ConfirmedAt = Now.AddDays(-2),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken()
        });
        await db.SaveChangesAsync();
        await OpenAlertAsync(db, seeded);
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        Assert.Single(notifier.To(seeded.Account.Email));
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.EventId == seeded.Event.Id));
    }

    [Fact]
    public async Task An_owner_who_unsubscribed_is_not_emailed_while_the_watch_is_kept()
    {
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db, Now);

        db.Subscriptions.Add(new Subscription
        {
            Email = seeded.Account.Email,
            EventId = seeded.Event.Id,
            AccountId = seeded.Account.Id,
            CreatedAt = Now.AddDays(-2),
            ConfirmedAt = Now.AddDays(-2),
            UnsubscribedAt = Now.AddDays(-1),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken()
        });
        await db.SaveChangesAsync();
        await OpenAlertAsync(db, seeded);
        var (delivery, notifier) = Build(db);

        await delivery.DeliverAsync();

        Assert.Empty(notifier.To(seeded.Account.Email));
    }

    [Fact]
    public async Task Two_owners_on_one_event_raise_one_reappearance_alert()
    {
        // The engine reads the wall clock, so this one is seeded against it.
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        var seeded = await SeedAsync(db, now);
        var other = new Account { Email = $"other-{Guid.NewGuid():N}@example.com", CreatedAt = now };
        db.Accounts.Add(other);
        await db.SaveChangesAsync();
        db.Watches.AddRange(
            new Watch { EventId = seeded.Event.Id, OwnerId = other.Id, CreatedAt = now },
            new Watch { EventId = seeded.Event.Id, OwnerId = null, CreatedAt = now });
        await db.SaveChangesAsync();

        var engine = new AlertEngine(db, null!, null!, Options.Create(new ReliabilityOptions()), NullLogger<AlertEngine>.Instance);
        var candidates = await engine.EvaluateWatchesAsync([seeded.Source]);

        Assert.Single(candidates, c => c.EventId == seeded.Event.Id && c.RuleKey == AlertRules.PrimaryReappeared);
    }
}
