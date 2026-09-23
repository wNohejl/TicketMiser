using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The account pages. Signed out: one form posting an address, with no password field
/// anywhere, and the same notice after every post. Signed in: the address, its watches linking
/// to their records, its purchases linking to their receipts, and a sign-out post carrying an
/// antiforgery token. A sign-in link's GET: one button posting back to itself, because a link
/// scanner must not spend the link.
/// </summary>
public class AccountPageTests : DeskTestContext
{
    /// <summary>What the endpoint renderer hands a form in production: the request's token.</summary>
    internal sealed class FakeAntiforgery : AntiforgeryStateProvider
    {
        public const string Value = "af-token-value";

        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new(Value, "__RequestVerificationToken");
    }

    public AccountPageTests() => Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgery>();

    private static AccountSummary Summary()
    {
        var venue = new Venue { Name = "Ryman Auditorium", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        var evt = new Event
        {
            Id = 7,
            Name = "Example Tour",
            Slug = "example-tour-ryman-2026-11-19",
            StartsAt = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
            Venue = venue
        };

        return new AccountSummary(
            new Account { Id = 3, Email = "fan@example.com" },
            [evt],
            [new Purchase { Id = 11, EventId = 7, Event = evt, OwnerId = 3, Quantity = 2, PaidPerTicket = 84.5m, AllIn = true }]);
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
    public void Signed_in_shows_the_address_its_watches_and_purchases_and_a_protected_sign_out()
    {
        var cut = Render<AccountPage>(p => p
            .Add(x => x.Kind, AccountPageKind.SignedIn)
            .Add(x => x.Summary, Summary()));

        Assert.Equal("Your account", cut.Find("h1").TextContent);
        Assert.Contains("fan@example.com", cut.Find("main").TextContent);

        var watch = Assert.Single(cut.FindAll(".account-watches a"));
        Assert.Equal("/e/example-tour-ryman-2026-11-19", watch.GetAttribute("href"));

        var receipt = Assert.Single(cut.FindAll(".account-purchases a"));
        Assert.Equal("/receipts/7.md", receipt.GetAttribute("href"));
        Assert.Contains("$84.50 all-in", cut.Find(".account-purchases").TextContent);

        var signOut = cut.Find("form[action='/account/sign-out']");
        Assert.Equal("post", signOut.GetAttribute("method"));
        Assert.Equal(FakeAntiforgery.Value, signOut.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
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
