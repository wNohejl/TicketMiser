using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Reports;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// The monthly report's own list, through the services the endpoints and the send-report
/// command call. Double opt-in and one-click unsubscribe as the alert list has them; a
/// published month goes once to each confirmed report subscriber and never again; an
/// unpublished month is refused; and an address that subscribed only to an event's alert is
/// never mailed the report (legal-guidelines rule 8).
///
/// <para>
/// The send reads every confirmed report subscriber in the shared database, so each test uses
/// its own month and asserts per address.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportMailingTests(PostgresFixture fixture) : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _reports = Directory.CreateTempSubdirectory("tm-reports-").FullName;

    public void Dispose() => Directory.Delete(_reports, recursive: true);

    private static Microsoft.Extensions.Options.IOptions<NotificationOptions> Options()
        => Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example/" });

    private static string Address() => $"reader-{Guid.NewGuid():N}@example.com";

    private (ReportSubscriptionService Subscriptions, ReportMailingService Mailing, RecordingNotifier Notifier, FakeClock Clock) Build(TicketMiserDbContext db)
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(Start);
        var subscriptions = new ReportSubscriptionService(db, notifier, Options(), clock, NullLogger<ReportSubscriptionService>.Instance);
        var mailing = new ReportMailingService(db, notifier, new FileReportLibrary(_reports), subscriptions, Options(), clock,
            NullLogger<ReportMailingService>.Instance);
        return (subscriptions, mailing, notifier, clock);
    }

    private void WriteReport(string month, bool published)
        => File.WriteAllText(Path.Combine(_reports, month + ReportMarkdown.FileSuffix), $"""
            ---
            published: {(published ? "true" : "false")}
            title: Fixture report for {month}
            ---
            # Fixture report for {month}

            A fixture summary for {month},
            not a measurement.

            ## Minutes to primary sellout

            | Event | Minutes |
            |---|---|
            | Example Tour | 1 |
            """);

    /// <summary>Subscribes and confirms through the service, as the form and the button would.</summary>
    private static async Task<ReportSubscription> ConfirmedAsync(TicketMiserDbContext db, ReportSubscriptionService service, string email)
    {
        await service.SubscribeAsync(email);
        var sub = await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Email == email);
        Assert.True(await service.ConfirmAsync(sub.ConfirmToken));
        return await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
    }

    [Fact]
    public async Task Subscribe_confirm_unsubscribe()
    {
        await using var db = fixture.CreateContext();
        var (service, _, notifier, clock) = Build(db);
        var email = Address();

        Assert.Equal(ReportSubscribeOutcome.Accepted, await service.SubscribeAsync($"  {email.ToUpperInvariant()} "));

        var sub = await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Email == email);
        Assert.Null(sub.ConfirmedAt);
        Assert.Equal(Start, sub.ConfirmationSentAt);
        Assert.Equal(43, sub.ConfirmToken.Length);
        Assert.NotEqual(sub.ConfirmToken, sub.UnsubscribeToken);

        var confirmation = Assert.Single(notifier.To(email));
        Assert.Contains($"https://tix.example/r/confirm/{sub.ConfirmToken}", confirmation.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://tix.example/r/unsubscribe/{sub.UnsubscribeToken}", confirmation.TextBody, StringComparison.Ordinal);
        Assert.Contains("monthly Nashville on-sale report", confirmation.TextBody, StringComparison.Ordinal);
        Assert.Equal($"<https://tix.example/r/unsubscribe/{sub.UnsubscribeToken}>", confirmation.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", confirmation.Headers["List-Unsubscribe-Post"]);

        // A repeat inside the hour reuses the row and sends nothing.
        clock.Advance(TimeSpan.FromMinutes(10));
        await service.SubscribeAsync(email);
        Assert.Single(notifier.To(email));

        // The GET that draws the button reads only; the POST confirms.
        Assert.True(await service.IsConfirmableAsync(sub.ConfirmToken));
        Assert.Null((await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).ConfirmedAt);
        Assert.True(await service.ConfirmAsync(sub.ConfirmToken));
        var confirmed = await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(Start.AddMinutes(10), confirmed.ConfirmedAt);
        Assert.True(confirmed.IsActive);

        // Confirmed: a repeat subscribe sends nothing and answers the same.
        Assert.Equal(ReportSubscribeOutcome.Accepted, await service.SubscribeAsync(email));
        Assert.Single(notifier.To(email));

        Assert.True(await service.UnsubscribeAsync(sub.UnsubscribeToken));
        Assert.False((await db.ReportSubscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).IsActive);

        // The old confirmation link neither shows a button nor brings the address back.
        Assert.False(await service.IsConfirmableAsync(sub.ConfirmToken));
        Assert.False(await service.ConfirmAsync(sub.ConfirmToken));
    }

    [Fact]
    public async Task An_invalid_address_stores_and_sends_nothing_and_unknown_tokens_find_nothing()
    {
        await using var db = fixture.CreateContext();
        var (service, _, notifier, _) = Build(db);

        Assert.Equal(ReportSubscribeOutcome.InvalidAddress, await service.SubscribeAsync("not an address"));
        Assert.Empty(notifier.Sent);

        Assert.False(await service.IsConfirmableAsync(SubscriptionService.NewToken()));
        Assert.False(await service.ConfirmAsync(SubscriptionService.NewToken()));
        Assert.False(await service.UnsubscribeAsync(SubscriptionService.NewToken()));
    }

    [Fact]
    public async Task Confirming_links_the_account_with_the_same_address()
    {
        await using var db = fixture.CreateContext();
        var (service, _, _, _) = Build(db);
        var email = Address();
        var account = new Account { Email = email, CreatedAt = Start };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var sub = await ConfirmedAsync(db, service, email);

        Assert.Equal(account.Id, sub.AccountId);
    }

    [Fact]
    public async Task A_published_month_goes_once_to_each_confirmed_report_subscriber_and_never_again()
    {
        const string month = "2030-01";
        WriteReport(month, published: true);

        await using var db = fixture.CreateContext();
        var (service, mailing, notifier, clock) = Build(db);

        var first = await ConfirmedAsync(db, service, Address());
        var second = await ConfirmedAsync(db, service, Address());

        var pending = Address();
        await service.SubscribeAsync(pending);

        var gone = await ConfirmedAsync(db, service, Address());
        await service.UnsubscribeAsync(gone.UnsubscribeToken);

        clock.Advance(TimeSpan.FromDays(1));
        var result = await mailing.SendAsync(month);

        Assert.Equal(ReportSendOutcome.Ran, result.Outcome);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Sent >= 2);

        foreach (var sub in new[] { first, second })
        {
            var mail = notifier.To(sub.Email).Where(m => m.Subject == $"Fixture report for {month}").ToList();
            var report = Assert.Single(mail);
            Assert.Contains($"A fixture summary for {month}, not a measurement.", report.TextBody, StringComparison.Ordinal);
            Assert.Contains($"https://tix.example/reports/{month}", report.TextBody, StringComparison.Ordinal);
            Assert.Contains($"https://tix.example/r/unsubscribe/{sub.UnsubscribeToken}", report.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("| Example Tour |", report.TextBody, StringComparison.Ordinal);
            Assert.Null(report.HtmlBody);

            var delivery = await db.ReportDeliveries.AsNoTracking().SingleAsync(d => d.ReportSubscriptionId == sub.Id);
            Assert.Equal(month, delivery.Month);
            Assert.Equal(Start.AddDays(1), delivery.SentAt);
            Assert.StartsWith("rec-", delivery.ProviderMessageId, StringComparison.Ordinal);
        }

        // Only the confirmation went to the unconfirmed address; nothing more to the unsubscribed one.
        Assert.Single(notifier.To(pending));
        Assert.Single(notifier.To(gone.Email));

        // A second run sends nothing for the same month.
        var before = notifier.Sent.Count;
        var again = await mailing.SendAsync(month);
        Assert.Equal(0, again.Sent);
        Assert.True(again.AlreadySent >= 2);
        Assert.Equal(before, notifier.Sent.Count);
        Assert.Equal(1, await db.ReportDeliveries.CountAsync(d => d.ReportSubscriptionId == first.Id && d.Month == month));
    }

    [Fact]
    public async Task Every_report_email_carries_the_one_click_unsubscribe_headers()
    {
        const string month = "2030-02";
        WriteReport(month, published: true);

        await using var db = fixture.CreateContext();
        var (service, mailing, notifier, _) = Build(db);
        var sub = await ConfirmedAsync(db, service, Address());

        await mailing.SendAsync(month);

        var report = Assert.Single(notifier.To(sub.Email), m => m.Subject == $"Fixture report for {month}");
        Assert.Equal($"<https://tix.example/r/unsubscribe/{sub.UnsubscribeToken}>", report.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", report.Headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public async Task An_unpublished_or_missing_month_is_refused_and_nothing_is_sent()
    {
        const string month = "2030-03";
        WriteReport(month, published: false);

        await using var db = fixture.CreateContext();
        var (service, mailing, notifier, _) = Build(db);
        var sub = await ConfirmedAsync(db, service, Address());
        var before = notifier.Sent.Count;

        Assert.Equal(ReportSendOutcome.NotPublished, (await mailing.SendAsync(month)).Outcome);
        Assert.Equal(ReportSendOutcome.NotPublished, (await mailing.SendAsync("2030-04")).Outcome);
        Assert.Equal(ReportSendOutcome.NotPublished, (await mailing.SendAsync("../../etc")).Outcome);

        Assert.Equal(before, notifier.Sent.Count);
        Assert.False(await db.ReportDeliveries.AnyAsync(d => d.ReportSubscriptionId == sub.Id));
    }

    [Fact]
    public async Task An_event_alert_subscriber_with_no_report_subscription_is_never_mailed_the_report()
    {
        const string month = "2030-05";
        WriteReport(month, published: true);

        await using var db = fixture.CreateContext();
        var (_, mailing, notifier, _) = Build(db);
        var email = Address();

        // Confirmed for an event's alert, and nothing else.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var venue = new Venue { Name = $"Ryman {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();
        var evt = new Event { Name = $"Show {suffix}", Slug = $"show-{suffix}", VenueId = venue.Id, StartsAt = Start.AddDays(30) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            Email = email,
            EventId = evt.Id,
            CreatedAt = Start,
            ConfirmedAt = Start,
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken()
        });
        await db.SaveChangesAsync();

        var result = await mailing.SendAsync(month);

        Assert.Equal(ReportSendOutcome.Ran, result.Outcome);
        Assert.Empty(notifier.To(email));
        Assert.False(await db.ReportSubscriptions.AnyAsync(s => s.Email == email));
    }

    private static async Task Lock(NpgsqlConnection connection, string function)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection);
        command.Parameters.AddWithValue("key", ReportMailingService.AdvisoryLockKey);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_send_while_another_process_holds_the_lock_sends_nothing()
    {
        const string month = "2030-06";
        WriteReport(month, published: true);

        await using var db = fixture.CreateContext();
        var (service, mailing, notifier, _) = Build(db);
        var sub = await ConfirmedAsync(db, service, Address());

        await using (var other = new NpgsqlConnection(fixture.ConnectionString))
        {
            await other.OpenAsync();
            await Lock(other, "pg_advisory_lock");

            Assert.Equal(ReportSendOutcome.Locked, (await mailing.SendAsync(month)).Outcome);
            Assert.DoesNotContain(notifier.To(sub.Email), m => m.Subject == $"Fixture report for {month}");

            // Released explicitly: a pooled connection keeps its session, and its locks, when closed.
            await Lock(other, "pg_advisory_unlock");
        }

        // Once the other process lets go, the month goes out.
        Assert.Equal(ReportSendOutcome.Ran, (await mailing.SendAsync(month)).Outcome);
        Assert.Single(notifier.To(sub.Email), m => m.Subject == $"Fixture report for {month}");
    }
}
