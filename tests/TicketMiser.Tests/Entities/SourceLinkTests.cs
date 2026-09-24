using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Tests.Integration;

namespace TicketMiser.Tests.Entities;

/// <summary>
/// The affiliate link a price cell carries, and the plain one everything else keeps. With a
/// programme configured, <see cref="SourceLink.Tagged"/> wraps the canonical event page in the
/// programme's Impact deep link with the destination percent-encoded exactly once; with none — or
/// with any id missing — it is the canonical page, so an half-configured programme never
/// publishes a broken link. A Ticketmaster price is only ever tagged for Ticketmaster's own
/// programme (rule 6). Receipts and alert emails cite the canonical page whatever is configured.
/// The rendered cells are pinned in TicketMiser.Desk.Tests.SourceLinkRenderTests.
/// </summary>
public class SourceLinkTests
{
    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source TicketmasterInventory = new() { Id = 3, Key = "ticketmaster-inventory", Name = "Ticketmaster Inventory Status", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/inventory-status/v1/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };
    private static readonly Source StubHub = new() { Id = 4, Key = "stubhub", Name = "StubHub", Kind = SourceKind.Resale, BaseUrl = "https://api.stubhub.example/" };

    private const string TicketmasterTemplate = "https://ticketmaster.evyy.net/c/{publisherId}/{adId}/{programId}?u={url}";
    private const string SeatGeekTemplate = "https://seatgeek.pxf.io/c/{publisherId}/{adId}/{programId}?u={url}";

    private static Event Evt(string tmId = "Z7r9jZ1A7-Fo?x&y") => new()
    {
        Id = 7,
        Name = "Example Tour",
        Slug = "example-performer-ryman-auditorium-2026-11-20",
        StartsAt = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero),
        OnSaleAt = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero),
        Venue = new Venue { Name = "Ryman Auditorium", City = "Nashville", State = "TN", Timezone = "America/Chicago" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = tmId, ["seatgeek"] = "6543210" }
    };

    /// <summary>Dummy ids, never real ones.</summary>
    public static AffiliateOptions Configured(bool ticketmaster = true, bool seatGeek = true) => new()
    {
        Ticketmaster = new AffiliateProgram { Enabled = ticketmaster, Template = TicketmasterTemplate, PublisherId = "1111111", AdId = "222222", ProgramId = "4272" },
        SeatGeek = new AffiliateProgram { Enabled = seatGeek, Template = SeatGeekTemplate, PublisherId = "1111111", AdId = "333333", ProgramId = "5555" }
    };

    /// <summary>The value of <c>u</c> in a tracking link, decoded once.</summary>
    private static string Destination(string link)
    {
        var query = new Uri(link).Query.TrimStart('?');
        var u = query.Split('&').Single(p => p.StartsWith("u=", StringComparison.Ordinal))[2..];
        return Uri.UnescapeDataString(u);
    }

    [Fact]
    public void A_configured_programme_wraps_the_canonical_page_in_its_deep_link()
    {
        var evt = Evt("tm-123");

        Assert.Equal(
            "https://ticketmaster.evyy.net/c/1111111/222222/4272?u=https%3A%2F%2Fwww.ticketmaster.com%2Fevent%2Ftm-123",
            SourceLink.Tagged(Ticketmaster, evt, Configured()));
        Assert.Equal(
            "https://seatgeek.pxf.io/c/1111111/333333/5555?u=https%3A%2F%2Fseatgeek.com%2Fe%2Fevents%2F6543210",
            SourceLink.Tagged(SeatGeek, evt, Configured()));
    }

    [Fact]
    public void The_destination_is_encoded_exactly_once()
    {
        // An id that needs escaping in the canonical path: the canonical page escapes it once as a
        // path segment, and the deep link escapes that whole URL once as a query value. Decoding
        // u once gives back the canonical page, character for character.
        var evt = Evt();
        var canonical = SourceLink.For(Ticketmaster, evt);
        var tagged = SourceLink.Tagged(Ticketmaster, evt, Configured());

        Assert.Equal("https://www.ticketmaster.com/event/Z7r9jZ1A7-Fo%3Fx%26y", canonical);
        Assert.Equal(canonical, Destination(tagged));
        Assert.DoesNotContain("%25", Destination(tagged), StringComparison.Ordinal);
        Assert.Contains("u=https%3A%2F%2Fwww.ticketmaster.com%2Fevent%2FZ7r9jZ1A7-Fo%253Fx%2526y", tagged, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_Ticketmaster_source_is_tagged_for_Ticketmaster_and_only_for_Ticketmaster()
    {
        var evt = Evt("tm-123");

        Assert.StartsWith("https://ticketmaster.evyy.net/", SourceLink.Tagged(TicketmasterInventory, evt, Configured()), StringComparison.Ordinal);

        // Rule 6: with only SeatGeek's programme on, a Ticketmaster price is never routed through
        // anything but its own page.
        Assert.Equal("https://www.ticketmaster.com/event/tm-123", SourceLink.Tagged(Ticketmaster, evt, Configured(ticketmaster: false)));
        Assert.Null(new AffiliateOptions().For(StubHub));
        Assert.Equal(SourceLink.For(StubHub, evt), SourceLink.Tagged(StubHub, evt, Configured()));
    }

    [Theory]
    [InlineData(false, TicketmasterTemplate, "1111111", "Enabled off")]
    [InlineData(true, "", "1111111", "no template")]
    [InlineData(true, "https://ticketmaster.evyy.net/c/{publisherId}/{adId}/{programId}", "1111111", "no {url}")]
    [InlineData(true, TicketmasterTemplate, "", "an id the template names is blank")]
    [InlineData(true, "http://ticketmaster.evyy.net/c/{publisherId}/{adId}/{programId}?u={url}", "1111111", "not https")]
    public void Anything_short_of_configured_is_the_canonical_page(bool enabled, string template, string publisherId, string why)
    {
        var evt = Evt("tm-123");
        var options = new AffiliateOptions
        {
            Ticketmaster = new AffiliateProgram { Enabled = enabled, Template = template, PublisherId = publisherId, AdId = "222222", ProgramId = "4272" }
        };

        Assert.False(options.AnyConfigured, why);
        Assert.False(SourceLink.IsTagged(Ticketmaster, options), why);
        Assert.Equal("https://www.ticketmaster.com/event/tm-123", SourceLink.Tagged(Ticketmaster, evt, options));
        Assert.Equal("https://www.ticketmaster.com/event/tm-123", SourceLink.Tagged(Ticketmaster, evt, null));
    }

    [Fact]
    public void Nothing_is_configured_by_default()
    {
        var options = new AffiliateOptions();

        Assert.False(options.AnyConfigured);
        Assert.Equal(SourceLink.For(SeatGeek, Evt()), SourceLink.Tagged(SeatGeek, Evt(), options));
    }

    [Fact]
    public void The_options_bind_from_environment_style_keys()
    {
        // Affiliates__ticketmaster__Enabled in the environment is Affiliates:ticketmaster:Enabled
        // here; the binder matches the property case-insensitively.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Affiliates:ticketmaster:Enabled"] = "true",
                ["Affiliates:ticketmaster:Template"] = TicketmasterTemplate,
                ["Affiliates:ticketmaster:PublisherId"] = "1111111",
                ["Affiliates:ticketmaster:AdId"] = "222222",
                ["Affiliates:ticketmaster:ProgramId"] = "4272"
            })
            .Build();

        var options = config.GetSection(AffiliateOptions.SectionName).Get<AffiliateOptions>()!;

        Assert.True(options.Ticketmaster.IsConfigured);
        Assert.False(options.SeatGeek.IsConfigured);
        Assert.StartsWith("https://ticketmaster.evyy.net/c/1111111/222222/4272?u=", SourceLink.Tagged(Ticketmaster, Evt(), options), StringComparison.Ordinal);
    }

    [Fact]
    public void A_receipt_cites_the_canonical_page_whatever_is_configured()
    {
        var evt = Evt("tm-123");
        Source[] sources = [Ticketmaster, SeatGeek];
        OnSaleTick[] ticks =
        [
            new() { EventId = 7, SourceId = 1, MinutesFromOnSale = 0, ObservedAt = evt.OnSaleAt!.Value, PrimaryStatus = InventoryStatus.Available, Lowest = 59.5m, AllIn = true },
            new() { EventId = 7, SourceId = 2, MinutesFromOnSale = 5, ObservedAt = evt.OnSaleAt!.Value.AddMinutes(5), ListingCount = 120, Lowest = 140m, AllIn = false }
        ];
        var record = new OnSaleRecord(evt, sources, ticks, OnSaleRecord.Derive(ticks, sources));
        var line = new PurchaseLine(
            new Purchase { Id = 1, EventId = 7, SourceId = 2, Quantity = 2, PaidPerTicket = 212.4m, AllIn = true, PurchasedAt = evt.OnSaleAt!.Value.AddMinutes(20) },
            evt, SeatGeek, null, null, PurchaseGrade.NotYetPlayed, "The event has not been played yet.");

        // Configured or not: the receipt has no way to reach the options, which is the point.
        _ = Configured();
        var md = PurchaseReceipt.Write(record, [line], evt.OnSaleAt!.Value.AddDays(3));

        Assert.Contains("<https://www.ticketmaster.com/event/tm-123>", md, StringComparison.Ordinal);
        Assert.Contains("<https://seatgeek.com/e/events/6543210>", md, StringComparison.Ordinal);
        Assert.DoesNotContain("evyy.net", md, StringComparison.Ordinal);
        Assert.DoesNotContain("pxf.io", md, StringComparison.Ordinal);
    }

    // ---- the alert email ---------------------------------------------------------------------

    private static (AlertDeliveryService Delivery, Alert Alert, Subscription Sub) AlertFixture()
    {
        var notifier = new RecordingNotifier();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 2, 16, 0, 0, TimeSpan.Zero));
        var options = Microsoft.Extensions.Options.Options.Create(new NotificationOptions { PublicBaseUrl = "https://tix.example" });

        // AlertMessage reads no table, so no database stands behind these.
        var subscriptions = new SubscriptionService(null!, notifier, options, clock, NullLogger<SubscriptionService>.Instance);
        var delivery = new AlertDeliveryService(null!, notifier, subscriptions, options, clock, NullLogger<AlertDeliveryService>.Instance);

        var alert = new Alert { Id = 1, RuleKey = "primary_reappeared", Source = Ticketmaster, SourceId = 1, Event = Evt("tm-123"), EventId = 7, TriggeredAt = clock.GetUtcNow() };
        var sub = new Subscription { Id = 9, Email = "fan@example.com", EventId = 7, UnsubscribeToken = "unsub-token" };
        return (delivery, alert, sub);
    }

    [Fact]
    public void An_alert_email_cites_the_canonical_page()
    {
        var (delivery, alert, sub) = AlertFixture();

        var message = delivery.AlertMessage(alert, sub, alert.TriggeredAt);

        Assert.Contains("https://www.ticketmaster.com/event/tm-123", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("evyy.net", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_page_subscriber_is_told_they_asked_on_the_record_page()
    {
        var (delivery, alert, sub) = AlertFixture();

        var body = delivery.AlertMessage(alert, sub, alert.TriggeredAt, viaWatch: false).TextBody;

        Assert.Contains("You asked for this email on the record page.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/account", body, StringComparison.Ordinal);
        Assert.Contains("https://tix.example/s/unsubscribe/unsub-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watcher_is_told_the_email_comes_from_their_account_and_where_to_manage_it()
    {
        var (delivery, alert, sub) = AlertFixture();

        var message = delivery.AlertMessage(alert, sub, alert.TriggeredAt, viaWatch: true);

        Assert.DoesNotContain("record page", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("you watch this event from your TicketMiser account", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://tix.example/account", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://tix.example/s/unsubscribe/unsub-token", message.TextBody, StringComparison.Ordinal);
        Assert.Equal("<https://tix.example/s/unsubscribe/unsub-token>", message.Headers["List-Unsubscribe"]);
    }
}
