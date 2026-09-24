using TicketMiser.Web.Components.Pages;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The pages behind the links in a monthly-report email. Confirm and unsubscribe are each one
/// button posting back to the link (a mail scanner's GET must change nothing), the pages speak
/// of the report and not of any event alert, and what follows an unsubscribe never says
/// whether the token matched.
/// </summary>
public class ReportSubscriptionPageTests : DeskTestContext
{
    [Fact]
    public void The_confirmation_link_shows_one_button_posting_to_itself()
    {
        var cut = Render<ReportSubscriptionPage>(p => p
            .Add(x => x.Kind, ReportSubscriptionPageKind.ConfirmPrompt)
            .Add(x => x.Token, "abc_DEF-123"));

        var form = cut.Find("form");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/r/confirm/abc_DEF-123", form.GetAttribute("action"));
        Assert.Equal("submit", Assert.Single(form.QuerySelectorAll("button")).GetAttribute("type"));
        Assert.Contains("monthly Nashville on-sale report", cut.Find("main").TextContent);
    }

    [Fact]
    public void A_confirmation_says_what_the_address_is_for_and_links_the_reports()
    {
        var cut = Render<ReportSubscriptionPage>(p => p.Add(x => x.Kind, ReportSubscriptionPageKind.Confirmed));

        Assert.Equal("Subscription confirmed", cut.Find("h1").TextContent);
        Assert.Contains("used only for the monthly report", cut.Find("main").TextContent);
        Assert.DoesNotContain("face-value", cut.Find("main").TextContent);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/reports");
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void An_unknown_confirmation_link_says_so_and_offers_no_button()
    {
        var cut = Render<ReportSubscriptionPage>(p => p.Add(x => x.Kind, ReportSubscriptionPageKind.ConfirmUnknown));

        Assert.Equal("Link not recognised", cut.Find("h1").TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void The_unsubscribe_link_shows_one_button_posting_to_itself()
    {
        var cut = Render<ReportSubscriptionPage>(p => p
            .Add(x => x.Kind, ReportSubscriptionPageKind.UnsubscribePrompt)
            .Add(x => x.Token, "abc_DEF-123"));

        var form = cut.Find("form");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/r/unsubscribe/abc_DEF-123", form.GetAttribute("action"));
        Assert.Equal("submit", Assert.Single(form.QuerySelectorAll("button")).GetAttribute("type"));
    }

    [Fact]
    public void The_unsubscribed_page_names_no_address()
    {
        var cut = Render<ReportSubscriptionPage>(p => p.Add(x => x.Kind, ReportSubscriptionPageKind.Unsubscribed));

        Assert.Equal("Unsubscribed", cut.Find("h1").TextContent);
        Assert.Contains("If this link belonged to a report subscription", cut.Find("main").TextContent);
        Assert.Empty(cut.FindAll("form"));
        Assert.Equal("noindex", cut.Find("meta[name=robots]").GetAttribute("content"));
    }
}
