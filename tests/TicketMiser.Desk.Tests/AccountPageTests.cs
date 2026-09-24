using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Accounts;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The account pages. Signed out: one form posting an address, with no password field
/// anywhere, and the same notice after every post. Signed in: the address, its watches linking
/// to their records, its own purchases graded the way the desk grades them, savings with each
/// fee basis kept apart, a Log a purchase form per watched event, a delete per row and a
/// receipt per event — every post carrying an antiforgery token. A sign-in link's GET: one
/// button posting back to itself, because a link scanner must not spend the link.
/// </summary>
public class AccountPageTests : DeskTestContext
{
    /// <summary>What the endpoint renderer hands a form in production: the request's token.</summary>
    internal sealed class FakeAntiforgery : AntiforgeryStateProvider
    {
        public const string Value = "af-token-value";

        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new(Value, "__RequestVerificationToken");
    }

    private static readonly DateTimeOffset Now = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };
    private static readonly IReadOnlyDictionary<int, Source> Sources = new Dictionary<int, Source> { [1] = Ticketmaster, [2] = SeatGeek };

    public AccountPageTests() => Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgery>();

    private static Venue Ryman() => new() { Name = "Ryman Auditorium", City = "Nashville", State = "TN", Timezone = "America/Chicago" };

    /// <summary>Watched, not played yet.</summary>
    private static Event Upcoming() => new()
    {
        Id = 7,
        Name = "Example Tour",
        Slug = "example-tour-ryman-2026-12-19",
        StartsAt = Now.AddDays(18),
        Venue = Ryman(),
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-7", ["seatgeek"] = "sg-7" }
    };

    /// <summary>Played, with a resale day-of price on record.</summary>
    private static Event Played() => new()
    {
        Id = 8,
        Name = "Past Show",
        Slug = "past-show-ryman-2026-11-10",
        StartsAt = Now.AddDays(-21),
        Venue = Ryman(),
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-8", ["seatgeek"] = "sg-8" }
    };

    private static AccountSummary Summary(params Event[] watching)
        => new(new Account { Id = 3, Email = "fan@example.com" }, watching);

    /// <summary>An all-in resale purchase graded against SeatGeek's day-of, and a face-value one not played yet.</summary>
    private static AccountLedger Ledger(Event upcoming, Event played)
    {
        var graded = new Purchase
        {
            Id = 21,
            EventId = played.Id,
            Event = played,
            OwnerId = 3,
            SourceId = SeatGeek.Id,
            Quantity = 2,
            PaidPerTicket = 90m,
            AllIn = true,
            PurchasedAt = Now.AddDays(-30),
            SavingsVsFinal = 15m
        };
        var waiting = new Purchase
        {
            Id = 22,
            EventId = upcoming.Id,
            Event = upcoming,
            OwnerId = 3,
            SourceId = Ticketmaster.Id,
            Quantity = 1,
            PaidPerTicket = 84.5m,
            AllIn = false,
            PurchasedAt = Now.AddDays(-2)
        };
        var dayOf = new FinalPrice { EventId = played.Id, SourceId = SeatGeek.Id, Lowest = 105m, AllIn = true, Currency = "USD" };

        var lines = new[]
        {
            PurchaseLedger.Line(waiting, upcoming, [], Sources, Now),
            PurchaseLedger.Line(graded, played, [dayOf], Sources, Now)
        };

        var form = new PurchaseForm(upcoming, [Ticketmaster, SeatGeek], MarketBest.Compose(SourceKind.Primary, []), MarketBest.Compose(SourceKind.Resale, []));
        return new AccountLedger(lines, PurchaseLedger.Summarise(lines), [form]);
    }

    private IRenderedComponent<AccountPage> SignedIn(PurchaseFormState? form = null, AccountNotice notice = AccountNotice.None)
    {
        var upcoming = Upcoming();
        var played = Played();
        return Render<AccountPage>(p => p
            .Add(x => x.Kind, AccountPageKind.SignedIn)
            .Add(x => x.Summary, Summary(upcoming))
            .Add(x => x.Ledger, Ledger(upcoming, played))
            .Add(x => x.Form, form)
            .Add(x => x.Notice, notice));
    }

    [Fact]
    public void Signed_out_is_one_address_field_posting_to_sign_in_and_no_password()
    {
        var cut = Render<AccountPage>(p => p.Add(x => x.Kind, AccountPageKind.SignedOut));

        var form = cut.Find("form");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/sign-in", form.GetAttribute("action"));
        Assert.Equal("email", form.QuerySelector("input[name=email]")!.GetAttribute("type"));
        Assert.Empty(cut.FindAll("input[type=password]"));
        Assert.Empty(cut.FindAll("[role=status]"));
    }

    [Fact]
    public void After_a_post_the_notice_says_the_same_thing_for_every_address()
    {
        var cut = Render<AccountPage>(p => p
            .Add(x => x.Kind, AccountPageKind.SignedOut)
            .Add(x => x.Notice, AccountNotice.LinkSent));

        Assert.Contains("Check your inbox for a sign-in link", cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public void Signed_in_shows_the_address_its_watches_and_a_protected_sign_out()
    {
        var cut = SignedIn();

        Assert.Equal("Your account", cut.Find("h1").TextContent);
        Assert.Contains("fan@example.com", cut.Find("main").TextContent);

        var watch = Assert.Single(cut.FindAll(".account-watches a"));
        Assert.Equal("/e/example-tour-ryman-2026-12-19", watch.GetAttribute("href"));

        var signOut = cut.Find("form[action='/account/sign-out']");
        Assert.Equal("post", signOut.GetAttribute("method"));
        Assert.Equal(FakeAntiforgery.Value, signOut.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
    }

    [Fact]
    public void Each_watch_has_a_protected_stop_of_its_own_that_returns_to_the_account()
    {
        var cut = SignedIn();

        var watch = Assert.Single(cut.FindAll(".account-watches li"));
        Assert.Equal("7", watch.GetAttribute("data-watch"));

        var form = watch.QuerySelector("form")!;
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/watches/7/stop", form.GetAttribute("action"));
        Assert.Equal(FakeAntiforgery.Value, form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
        Assert.Equal("account", form.QuerySelector("input[name=from]")!.GetAttribute("value"));
        Assert.Contains("Stop watching", form.QuerySelector("button")!.TextContent);
    }

    [Fact]
    public void After_stopping_the_notice_says_the_email_stops_with_the_watch()
    {
        var cut = SignedIn(notice: AccountNotice.StoppedWatching);

        var notice = string.Join(' ', cut.Find("[role=status]").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("No longer watching", notice);
        Assert.Contains("alert email stops with the watch", notice);
        Assert.Contains("unless you asked for that email on the record page", notice);
    }

    [Fact]
    public void With_an_affiliate_configured_the_day_of_price_is_tagged_and_disclosed_and_where_it_was_bought_is_not()
    {
        Services.Configure<AffiliateOptions>(o => o.SeatGeek = new AffiliateProgram
        {
            Enabled = true,
            Template = "https://seatgeek.pxf.io/c/{publisherId}/{adId}/{programId}?u={url}",
            PublisherId = "1111111",
            AdId = "333333",
            ProgramId = "5555"
        });

        var cut = SignedIn();
        var graded = cut.FindAll(".account-purchase")[1];

        // Where the fan bought is evidence, like the receipt: the canonical page.
        Assert.Equal("https://seatgeek.com/e/events/sg-8", graded.QuerySelector("[data-cell=source]")!.GetAttribute("href"));

        // The day-of lowest is a price on offer: tagged, marked, and disclosed.
        var dayOf = graded.QuerySelector("[data-cell=day-of]")!;
        Assert.Equal("https://seatgeek.pxf.io/c/1111111/333333/5555?u=https%3A%2F%2Fseatgeek.com%2Fe%2Fevents%2Fsg-8", dayOf.GetAttribute("href"));
        Assert.Contains("sponsored", dayOf.GetAttribute("rel"));
        Assert.Contains("may earn TicketMiser a commission", cut.Find("[data-disclosure=affiliate]").TextContent);
    }

    [Fact]
    public void With_no_affiliate_the_day_of_price_is_the_canonical_page_and_nothing_is_disclosed()
    {
        var cut = SignedIn();

        Assert.Equal("https://seatgeek.com/e/events/sg-8", cut.Find("[data-cell=day-of]").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-disclosure]"));
    }

    [Fact]
    public void Each_purchase_links_its_event_and_source_and_says_its_fee_basis()
    {
        var cut = SignedIn();

        var rows = cut.FindAll(".account-purchase");
        Assert.Equal(["22", "21"], rows.Select(r => r.GetAttribute("data-purchase")));

        var waiting = rows[0];
        Assert.Equal("/e/example-tour-ryman-2026-12-19", waiting.QuerySelector(".account-purchase__head > a")!.GetAttribute("href"));
        Assert.Equal("Ticketmaster", waiting.QuerySelector("[data-cell=source]")!.TextContent);
        Assert.Contains("1 ticket at $84.50 per ticket, face value", waiting.QuerySelector("[data-cell=paid]")!.TextContent);

        var graded = rows[1];
        Assert.Equal("/e/past-show-ryman-2026-11-10", graded.QuerySelector(".account-purchase__head > a")!.GetAttribute("href"));
        Assert.Equal("SeatGeek", graded.QuerySelector("[data-cell=source]")!.TextContent);
        Assert.Contains("2 tickets at $90.00 per ticket, all-in", graded.QuerySelector("[data-cell=paid]")!.TextContent);
    }

    [Fact]
    public void A_graded_purchase_says_what_it_saved_and_an_ungraded_one_says_why_not()
    {
        var cut = SignedIn();
        var rows = cut.FindAll(".account-purchase");

        Assert.Contains("Not graded: The event has not been played yet.", rows[0].QuerySelector("[data-cell=grade]")!.TextContent);

        var grade = rows[1].QuerySelector("[data-cell=grade]")!;
        Assert.Contains("Saved $15.00 per ticket ($30.00 in all) against the day-of all-in price", grade.TextContent);
        Assert.Contains("day-of lowest $105.00 all-in at", grade.TextContent);

        // SeatGeek's numbers appear, so its name does, linked to seatgeek.com (legal rule 3).
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "https://seatgeek.com");
    }

    [Fact]
    public void Savings_total_all_in_and_face_value_apart_and_never_together()
    {
        var cut = SignedIn();

        var rows = cut.FindAll(".account-savings tbody tr");
        Assert.Equal(["all-in", "face"], rows.Select(r => r.GetAttribute("data-basis")));

        var allIn = rows[0].QuerySelectorAll("td").Select(td => td.TextContent).ToList();
        Assert.Equal(["1", "2", "$180.00", "1", "$30.00"], allIn);

        var face = rows[1].QuerySelectorAll("td").Select(td => td.TextContent).ToList();
        Assert.Equal(["1", "1", "$84.50", "0", "none graded"], face);

        // No row adds the two bases up: $264.50 is a sum nobody paid.
        Assert.DoesNotContain("264.50", cut.Find(".account-savings").TextContent);
        Assert.Contains("1 not played yet", cut.Find("[data-ungraded]").TextContent);
    }

    [Fact]
    public void Each_purchase_has_a_protected_delete_post_of_its_own()
    {
        var cut = SignedIn();

        foreach (var (row, id) in cut.FindAll(".account-purchase").Zip(["22", "21"]))
        {
            var form = row.QuerySelector("form")!;
            Assert.Equal("post", form.GetAttribute("method"));
            Assert.Equal($"/account/purchases/{id}/delete", form.GetAttribute("action"));
            Assert.Equal(FakeAntiforgery.Value, form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
        }
    }

    [Fact]
    public void Receipts_are_the_account_s_own_downloads_per_event()
    {
        var cut = SignedIn();

        var links = cut.FindAll(".account-receipts a");
        Assert.Equal(["/account/receipts/7.md", "/account/receipts/8.md"], links.Select(a => a.GetAttribute("href")));
        Assert.All(links, a => Assert.True(a.HasAttribute("download")));

        // Never the operator's receipt, which carries every purchase on the event.
        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href")?.StartsWith("/receipts/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Log_a_purchase_is_a_protected_form_per_watched_event_with_the_fee_basis_unanswered()
    {
        var cut = SignedIn();

        var form = Assert.Single(cut.FindAll(".account-log form"));
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/purchases", form.GetAttribute("action"));
        Assert.Equal(FakeAntiforgery.Value, form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
        Assert.Equal("7", form.QuerySelector("input[name=eventId]")!.GetAttribute("value"));

        var radios = form.QuerySelectorAll("input[name=allIn]");
        Assert.Equal(["true", "false"], radios.Select(r => r.GetAttribute("value")));
        Assert.All(radios, r => Assert.False(r.HasAttribute("checked")));
        Assert.True(radios[0].HasAttribute("required"));

        var options = form.QuerySelectorAll("select[name=sourceId] option").Select(o => o.TextContent).ToList();
        Assert.Equal(["Elsewhere", "Ticketmaster (primary)", "SeatGeek (resale)"], options);

        // Closed until asked for, or until a refused post draws it again.
        Assert.False(cut.Find("details#log-7").HasAttribute("open"));
    }

    [Fact]
    public void A_refused_post_draws_its_form_open_with_what_was_typed_and_what_was_wrong()
    {
        var input = new PurchaseFormInput(7, "2", "abc", null, "2", null, "floor");
        var errors = new Dictionary<string, string>
        {
            ["price"] = "The price per ticket, above zero.",
            ["allIn"] = "Say whether the price includes fees."
        };

        var cut = SignedIn(new PurchaseFormState(input, errors));

        Assert.Contains("The purchase was not logged", cut.Find("[role=alert]").TextContent);
        Assert.True(cut.Find("details#log-7").HasAttribute("open"));
        Assert.Equal("abc", cut.Find("#price-7").GetAttribute("value"));
        Assert.Equal("floor", cut.Find("#note-7").GetAttribute("value"));
        Assert.True(cut.Find("#source-7 option[value='2']").HasAttribute("selected"));
        Assert.Equal(["price", "allIn"], cut.FindAll("[data-error]").Select(e => e.GetAttribute("data-error")));
        Assert.Equal("true", cut.Find("#price-7").GetAttribute("aria-invalid"));
    }

    [Fact]
    public void The_notice_after_a_log_or_a_delete_says_which()
    {
        Assert.Contains("Purchase logged", SignedIn(notice: AccountNotice.PurchaseLogged).Find("[role=status]").TextContent);
        Assert.Contains("Purchase deleted", SignedIn(notice: AccountNotice.PurchaseDeleted).Find("[role=status]").TextContent);
    }

    [Fact]
    public void With_no_purchases_the_page_says_so_and_offers_no_receipt()
    {
        var upcoming = Upcoming();
        var cut = Render<AccountPage>(p => p
            .Add(x => x.Kind, AccountPageKind.SignedIn)
            .Add(x => x.Summary, Summary(upcoming))
            .Add(x => x.Ledger, new AccountLedger([], PurchaseLedger.Summarise([]),
                [new PurchaseForm(upcoming, [Ticketmaster], MarketBest.Compose(SourceKind.Primary, []), MarketBest.Compose(SourceKind.Resale, []))])));

        Assert.Contains("No purchases logged.", cut.Find("main").TextContent);
        Assert.Empty(cut.FindAll(".account-receipts"));
        Assert.Empty(cut.FindAll(".account-savings"));
        Assert.Single(cut.FindAll(".account-log form"));
    }

    [Fact]
    public void A_sign_in_link_shows_one_button_posting_to_itself_with_a_token()
    {
        var cut = Render<AccountPage>(p => p
            .Add(x => x.Kind, AccountPageKind.VerifyPrompt)
            .Add(x => x.Token, "abc_DEF-123"));

        var form = cut.Find("form");
        Assert.Equal("/account/verify/abc_DEF-123", form.GetAttribute("action"));
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.NotNull(form.QuerySelector("input[name=__RequestVerificationToken]"));
        Assert.Equal("submit", Assert.Single(form.QuerySelectorAll("button")).GetAttribute("type"));
    }

    [Fact]
    public void A_dead_link_offers_a_new_one_and_no_form()
    {
        var cut = Render<AccountPage>(p => p.Add(x => x.Kind, AccountPageKind.VerifyUnknown));

        Assert.Equal("Link not recognised", cut.Find("h1").TextContent);
        Assert.Empty(cut.FindAll("form"));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/account");
    }
}
