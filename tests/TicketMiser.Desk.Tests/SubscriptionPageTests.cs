using TicketMiser.Web.Components.Pages;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The pages behind the links in a subscription email. The unsubscribe link shows one button
/// that posts back to itself (a link scanner's GET must not unsubscribe anyone), and what
/// follows the post never says whether the token matched a subscription.
/// </summary>
public class SubscriptionPageTests : DeskTestContext
{
    [Fact]
    public void The_unsubscribe_link_shows_one_button_posting_to_itself()
    {
        var cut = RenderComponent<SubscriptionPage>(p => p
            .Add(x => x.Kind, SubscriptionPageKind.UnsubscribePrompt)
            .Add(x => x.Token, "abc_DEF-123"));

        var form = cut.Find("form");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/s/unsubscribe/abc_DEF-123", form.GetAttribute("action"));
        Assert.Equal("submit", Assert.Single(form.QuerySelectorAll("button")).GetAttribute("type"));
    }

    [Fact]
    public void The_unsubscribed_page_names_no_event_and_no_address()
    {
        var cut = RenderComponent<SubscriptionPage>(p => p.Add(x => x.Kind, SubscriptionPageKind.Unsubscribed));

        Assert.Equal("Unsubscribed", cut.Find("h1").TextContent);
        Assert.Contains("If this link belonged to a subscription", cut.Find("main").TextContent);
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public void A_confirmation_names_the_event_and_links_back_to_its_record()
    {
        var cut = RenderComponent<SubscriptionPage>(p => p
            .Add(x => x.Kind, SubscriptionPageKind.Confirmed)
            .Add(x => x.EventName, "Example Tour")
            .Add(x => x.Slug, "example-tour-bridgestone-arena-2026-11-19"));

        Assert.Contains("Example Tour", cut.Find("main").TextContent);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/e/example-tour-bridgestone-arena-2026-11-19");
    }
}
