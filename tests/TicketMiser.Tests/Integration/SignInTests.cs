using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability.Accounts;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Accounts;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// Magic-link sign-in through the service the endpoints call: the link is emailed and only its
/// hash is stored; it signs in once and not after fifteen minutes; every address gets the same
/// answer and no account exists until a link is followed; the post is rate-limited; and a
/// first sign-in turns the address's confirmed subscriptions into owned watches.
/// </summary>
[Collection(PostgresCollection.Name)]
public partial class SignInTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"/account/verify/(?<token>[A-Za-z0-9_-]{43})")]
    private static partial Regex LinkPattern();

    private static (AccountService Service, RecordingNotifier Notifier, FakeClock Clock) Build(TicketMiserDbContext db)
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(Start);
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example" });
        return (new AccountService(db, notifier, options, clock, NullLogger<AccountService>.Instance), notifier, clock);
    }

    private static string Address() => $"fan-{Guid.NewGuid():N}@example.com";

    private static string TokenIn(MailMessage message) => LinkPattern().Match(message.TextBody).Groups["token"].Value;

    [Fact]
    public async Task The_link_is_emailed_and_only_its_hash_is_stored()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, _) = Build(db);
        var email = Address();

        Assert.Equal(SignInRequestOutcome.Accepted, await service.RequestSignInAsync($"  {email.ToUpperInvariant()} "));

        var message = Assert.Single(notifier.To(email));
        var token = TokenIn(message);
        Assert.Equal(43, token.Length);
        Assert.Contains($"https://tix.example/account/verify/{token}", message.TextBody, StringComparison.Ordinal);

        var row = await db.SignInTokens.AsNoTracking().SingleAsync(t => t.Email == email);
        Assert.Equal(AccountService.Hash(token), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.NotEqual(token, row.TokenHash);
        Assert.False(await db.SignInTokens.AnyAsync(t => t.TokenHash == token));
        Assert.Equal(Start + TimeSpan.FromMinutes(15), row.ExpiresAt);
    }

    [Fact]
    public async Task A_link_signs_in_once_and_creates_the_account_on_first_use()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, _) = Build(db);
        var email = Address();

        await service.RequestSignInAsync(email);
        Assert.False(await db.Accounts.AnyAsync(a => a.Email == email));

        var token = TokenIn(Assert.Single(notifier.To(email)));
        Assert.True(await service.IsRedeemableAsync(token));

        var account = await service.RedeemAsync(token);
        Assert.NotNull(account);
        Assert.Equal(email, account.Email);
        Assert.Equal(Start, account.LastSignInAt);

        Assert.Null(await service.RedeemAsync(token));
        Assert.False(await service.IsRedeemableAsync(token));
        Assert.Equal(1, await db.Accounts.CountAsync(a => a.Email == email));
    }

    [Fact]
    public async Task A_second_link_to_a_known_address_signs_in_the_same_account()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, clock) = Build(db);
        var email = Address();

        await service.RequestSignInAsync(email);
        var first = await service.RedeemAsync(TokenIn(notifier.To(email)[0]));

        clock.Advance(TimeSpan.FromMinutes(2));
        await service.RequestSignInAsync(email);
        var second = await service.RedeemAsync(TokenIn(notifier.To(email)[1]));

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task A_link_does_not_sign_in_after_fifteen_minutes()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, clock) = Build(db);
        var email = Address();

        await service.RequestSignInAsync(email);
        var token = TokenIn(Assert.Single(notifier.To(email)));

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.False(await service.IsRedeemableAsync(token));
        Assert.Null(await service.RedeemAsync(token));
        Assert.False(await db.Accounts.AnyAsync(a => a.Email == email));
    }

    [Fact]
    public async Task An_unknown_token_signs_nobody_in()
    {
        await using var db = fixture.CreateContext();
        var (service, _, _) = Build(db);

        Assert.Null(await service.RedeemAsync(SubscriptionService.NewToken()));
    }

    [Fact]
    public async Task A_new_address_and_a_known_one_get_the_same_answer_and_an_invalid_one_gets_nothing()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, _) = Build(db);

        var known = Address();
        db.Accounts.Add(new Account { Email = known, CreatedAt = Start.AddDays(-3) });
        await db.SaveChangesAsync();
        var unknown = Address();

        Assert.Equal(SignInRequestOutcome.Accepted, await service.RequestSignInAsync(known));
        Assert.Equal(SignInRequestOutcome.Accepted, await service.RequestSignInAsync(unknown));
        Assert.Single(notifier.To(known));
        Assert.Single(notifier.To(unknown));
        Assert.Equal(notifier.To(known)[0].Subject, notifier.To(unknown)[0].Subject);

        Assert.Equal(SignInRequestOutcome.InvalidAddress, await service.RequestSignInAsync("not an address"));
        Assert.Equal(SignInRequestOutcome.InvalidAddress, await service.RequestSignInAsync("Someone <x@example.com>"));
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task A_repeat_request_inside_a_minute_sends_nothing_more()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, clock) = Build(db);
        var email = Address();

        await service.RequestSignInAsync(email);
        await service.RequestSignInAsync(email);
        Assert.Single(notifier.To(email));

        clock.Advance(AccountService.ResendAfter + TimeSpan.FromSeconds(1));
        await service.RequestSignInAsync(email);
        Assert.Equal(2, notifier.To(email).Count);
    }

    [Fact]
    public void The_sign_in_post_is_limited_to_ten_per_client_per_ten_minutes()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");

        var partition = AccountEndpoints.SignInPartition(http);
        Assert.Equal("203.0.113.9", partition.PartitionKey);

        using var limiter = partition.Factory(partition.PartitionKey);
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.AttemptAcquire().IsAcquired, $"post {i + 1} should pass");

        Assert.False(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public async Task A_first_sign_in_turns_the_address_s_confirmed_subscriptions_into_owned_watches()
    {
        await using var db = fixture.CreateContext();
        var (service, notifier, _) = Build(db);
        var email = Address();

        var venue = new Venue { Name = $"Station Inn {Guid.NewGuid():N}", City = "Nashville", State = "TN" };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();
        var confirmed = new Event { Name = "Confirmed", VenueId = venue.Id, StartsAt = Start.AddDays(10) };
        var pending = new Event { Name = "Pending", VenueId = venue.Id, StartsAt = Start.AddDays(11) };
        var gone = new Event { Name = "Gone", VenueId = venue.Id, StartsAt = Start.AddDays(12) };
        db.Events.AddRange(confirmed, pending, gone);
        await db.SaveChangesAsync();

        Subscription Sub(Event e, bool isConfirmed, bool unsubscribed) => new()
        {
            Email = email,
            EventId = e.Id,
            CreatedAt = Start.AddDays(-2),
            ConfirmToken = SubscriptionService.NewToken(),
            UnsubscribeToken = SubscriptionService.NewToken(),
            ConfirmedAt = isConfirmed ? Start.AddDays(-2) : null,
            UnsubscribedAt = unsubscribed ? Start.AddDays(-1) : null
        };
        db.Subscriptions.AddRange(Sub(confirmed, true, false), Sub(pending, false, false), Sub(gone, true, true));
        await db.SaveChangesAsync();

        await service.RequestSignInAsync(email);
        var account = await service.RedeemAsync(TokenIn(Assert.Single(notifier.To(email))));
        Assert.NotNull(account);

        var watches = await db.Watches.AsNoTracking().Where(w => w.OwnerId == account.Id).ToListAsync();
        var watch = Assert.Single(watches);
        Assert.Equal(confirmed.Id, watch.EventId);
        Assert.True(watch.Enabled);

        // The subscription row stays — it carries the unsubscribe token — and names its account.
        var claimed = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Email == email && s.EventId == confirmed.Id);
        Assert.Equal(account.Id, claimed.AccountId);
        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Email == email && s.EventId == pending.Id)).AccountId);

        // A second sign-in claims nothing twice.
        Assert.Equal(0, await service.ClaimSubscriptionsAsync(account));
    }

    [Fact]
    public async Task Watching_makes_an_owned_watch_and_the_alert_subscription_that_goes_with_it()
    {
        await using var db = fixture.CreateContext();
        var (service, _, _) = Build(db);
        var email = Address();

        var venue = new Venue { Name = $"Exit/In {Guid.NewGuid():N}", City = "Nashville", State = "TN" };
        var account = new Account { Email = email, CreatedAt = Start };
        db.AddRange(venue, account);
        await db.SaveChangesAsync();
        var evt = new Event { Name = "Watched", Slug = $"watched-{Guid.NewGuid():N}", VenueId = venue.Id, StartsAt = Start.AddDays(9) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var result = await service.WatchAsync(account.Id, evt.Id);
        Assert.Equal(evt.Slug, result!.Slug);
        await service.WatchAsync(account.Id, evt.Id);

        var watch = await db.Watches.AsNoTracking().SingleAsync(w => w.EventId == evt.Id);
        Assert.Equal(account.Id, watch.OwnerId);

        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);
        Assert.Equal(email, sub.Email);
        Assert.Equal(account.Id, sub.AccountId);
        Assert.True(sub.IsActive);

        Assert.Null(await service.WatchAsync(account.Id, int.MaxValue));
        Assert.Null(await service.WatchAsync(int.MaxValue, evt.Id));
    }
}
