using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// Double opt-in through the service the endpoints call: subscribe sends one confirmation and
/// nothing else, a repeat reuses the row and its tokens and is not resent inside the hour,
/// confirm and unsubscribe move the row by token, and an unknown token finds nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SubscriptionFlowTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static (SubscriptionService Service, RecordingNotifier Notifier, FakeClock Clock) Build(TicketMiserDbContext db)
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(Start);
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example/" });

        return (new SubscriptionService(db, notifier, options, clock, NullLogger<SubscriptionService>.Instance), notifier, clock);
    }

    private static async Task<Event> SeedEventAsync(TicketMiserDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var venue = new Venue { Name = $"Ascend Amphitheater {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();

        var evt = new Event { Name = $"Show {suffix}", Slug = $"show-{suffix}", VenueId = venue.Id, StartsAt = Start.AddDays(40) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt;
    }

    [Fact]
    public async Task Subscribe_confirm_unsubscribe()
    {
        await using var db = fixture.CreateContext();
        var evt = await SeedEventAsync(db);
        var (service, notifier, _) = Build(db);
        var email = $"fan-{evt.Slug}@example.com";

        var result = await service.SubscribeAsync(evt.Slug!, $"  {email.ToUpperInvariant()} ");

        Assert.Equal(SubscribeOutcome.Accepted, result.Outcome);
        Assert.Equal(evt.Slug, result.Slug);

        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);
        Assert.Equal(email, sub.Email);
        Assert.Null(sub.ConfirmedAt);
        Assert.Equal(Start, sub.ConfirmationSentAt);
        Assert.Equal(43, sub.ConfirmToken.Length);
        Assert.NotEqual(sub.ConfirmToken, sub.UnsubscribeToken);

        var confirmation = Assert.Single(notifier.To(email));
        Assert.Contains($"https://tix.example/s/confirm/{sub.ConfirmToken}", confirmation.TextBody, StringComparison.Ordinal);
        Assert.Contains($"https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}", confirmation.TextBody, StringComparison.Ordinal);
        Assert.Equal($"<https://tix.example/s/unsubscribe/{sub.UnsubscribeToken}>", confirmation.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", confirmation.Headers["List-Unsubscribe-Post"]);

        var confirmed = await service.ConfirmAsync(sub.ConfirmToken);
        Assert.NotNull(confirmed);
        Assert.Equal(evt.Name, confirmed.EventName);
        Assert.Equal(evt.Slug, confirmed.Slug);
        Assert.NotNull((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).ConfirmedAt);

        // Confirmed: a repeat subscribe sends nothing and says nothing different.
        Assert.Equal(SubscribeOutcome.Accepted, (await service.SubscribeAsync(evt.Slug!, email)).Outcome);
        Assert.Single(notifier.To(email));

        Assert.True(await service.UnsubscribeAsync(sub.UnsubscribeToken));
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.NotNull(after.UnsubscribedAt);
        Assert.False(after.IsActive);

        // The old confirmation link does not bring an unsubscribed address back.
        Assert.Null(await service.ConfirmAsync(sub.ConfirmToken));
    }

    [Fact]
    public async Task A_duplicate_subscribe_reuses_the_tokens_and_is_not_resent_within_the_hour()
    {
        await using var db = fixture.CreateContext();
        var evt = await SeedEventAsync(db);
        var (service, notifier, clock) = Build(db);
        var email = $"twice-{evt.Slug}@example.com";

        await service.SubscribeAsync(evt.Slug!, email);
        var first = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);

        clock.Advance(TimeSpan.FromMinutes(20));
        await service.SubscribeAsync(evt.Slug!, email);

        var second = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ConfirmToken, second.ConfirmToken);
        Assert.Equal(first.UnsubscribeToken, second.UnsubscribeToken);
        Assert.Single(notifier.To(email));

        // Past the hour, the same confirmation link goes out again.
        clock.Advance(TimeSpan.FromMinutes(41));
        await service.SubscribeAsync(evt.Slug!, email);

        var resent = notifier.To(email);
        Assert.Equal(2, resent.Count);
        Assert.Contains(first.ConfirmToken, resent[1].TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coming_back_after_unsubscribing_is_a_fresh_opt_in_with_fresh_tokens()
    {
        await using var db = fixture.CreateContext();
        var evt = await SeedEventAsync(db);
        var (service, notifier, _) = Build(db);
        var email = $"back-{evt.Slug}@example.com";

        await service.SubscribeAsync(evt.Slug!, email);
        var first = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);
        await service.ConfirmAsync(first.ConfirmToken);
        await service.UnsubscribeAsync(first.UnsubscribeToken);

        await service.SubscribeAsync(evt.Slug!, email);

        var again = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.EventId == evt.Id);
        Assert.Null(again.ConfirmedAt);
        Assert.Null(again.UnsubscribedAt);
        Assert.NotEqual(first.ConfirmToken, again.ConfirmToken);
        Assert.NotEqual(first.UnsubscribeToken, again.UnsubscribeToken);
        Assert.Equal(2, notifier.To(email).Count);
    }

    [Fact]
    public async Task An_invalid_address_or_an_unknown_event_stores_and_sends_nothing()
    {
        await using var db = fixture.CreateContext();
        var evt = await SeedEventAsync(db);
        var (service, notifier, _) = Build(db);

        Assert.Equal(SubscribeOutcome.InvalidAddress, (await service.SubscribeAsync(evt.Slug!, "not an address")).Outcome);
        Assert.Equal(SubscribeOutcome.EventNotFound, (await service.SubscribeAsync($"{evt.Slug}-nope", "fan@example.com")).Outcome);

        Assert.Equal(0, await db.Subscriptions.CountAsync(s => s.EventId == evt.Id));
        Assert.Empty(notifier.To("fan@example.com"));
    }

    [Fact]
    public async Task Unknown_tokens_find_nothing()
    {
        await using var db = fixture.CreateContext();
        var (service, _, _) = Build(db);

        Assert.Null(await service.ConfirmAsync(SubscriptionService.NewToken()));
        Assert.False(await service.UnsubscribeAsync(SubscriptionService.NewToken()));
    }

    [Theory]
    [InlineData(" Fan@Example.COM ", "fan@example.com")]
    [InlineData("a.b+tag@mail.example.org", "a.b+tag@mail.example.org")]
    [InlineData("Fan <fan@example.com>", null)]
    [InlineData("fan@localhost", null)]
    [InlineData("fan@example.com\r\nBcc: x@example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Addresses_are_normalised_or_refused(string? input, string? expected)
        => Assert.Equal(expected, SubscriptionService.NormaliseEmail(input));

    [Fact]
    public void An_address_longer_than_the_column_is_refused()
        => Assert.Null(SubscriptionService.NormaliseEmail(new string('a', 250) + "@example.com"));
}
