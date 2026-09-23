using Bunit;
using TicketMiser.Core.Analytics;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Purchases window: that an empty ledger says how a purchase comes to exist, that a graded
/// row shows the day-of price and the saving coloured on the value only, that an ungraded row says
/// why, that the filter narrows to graded or ungraded, that delete asks first as a destructive
/// alert and does nothing on "Keep it", and that the receipt is fetched as a file for the event.
/// </summary>
public class PurchasesPanelTests : PurchaseBench
{
    private IRenderedComponent<PurchasesPanel> Render(params PurchaseLine[] lines)
    {
        UseLedger(new FakePurchaseQueries(lines));
        return Render<PurchasesPanel>();
    }

    private (PurchaseLine Graded, PurchaseLine Ungraded) Two()
    {
        var played = Ryman(7, "Played Show", daysFromNow: -3);
        var upcoming = Ryman(8, "Upcoming Show", daysFromNow: 20);

        return (
            Line(Bought(1, played, SeatGeek, 80m, allIn: true, quantity: 2, saved: 15m), played, Final(played, SeatGeek, 95m, allIn: true)),
            Line(Bought(2, upcoming, Ticketmaster, 59.5m, allIn: false, quantity: 4), upcoming));
    }

    private static AngleSharp.Dom.IElement RowFor(IRenderedComponent<PurchasesPanel> cut, string eventName)
        => cut.FindAll("tbody .mud-table-row").Single(r => r.TextContent.Contains(eventName, StringComparison.Ordinal));

    [Fact]
    public void An_empty_ledger_says_how_a_purchase_comes_to_exist_and_offers_the_Watchlist()
    {
        var cut = Render();

        var empty = cut.Find(".empty--new");
        Assert.Contains("No purchases logged", empty.TextContent);
        Assert.Contains("Log purchase", empty.TextContent);

        cut.Find(".empty__action button").Click();
        Assert.Single(Windows.Windows, w => w.Definition.Key == WindowCatalog.Watchlist);
    }

    [Fact]
    public void A_graded_row_shows_the_day_of_price_and_the_saving_coloured_on_the_value_only()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        var row = RowFor(cut, "Played Show");
        Assert.Contains("$80.00", row.QuerySelector("[data-cell=paid]")!.TextContent);
        Assert.Contains("all-in", row.TextContent);
        Assert.Contains("$160.00", row.TextContent);
        Assert.Contains("$95.00", row.QuerySelector("[data-cell=dayof]")!.TextContent);

        var per = row.QuerySelector("[data-cell=saved-per]")!;
        var total = row.QuerySelector("[data-cell=saved-total]")!;
        Assert.Contains("+$15.00", per.TextContent);
        Assert.Contains("+$30.00", total.TextContent);
        Assert.Contains("up", per.ClassList);
        Assert.Contains("up", total.ClassList);

        // Hue is on the value, not on the row or the controls.
        Assert.DoesNotContain("up", row.ClassList);

        // The source is a link to the event at that source.
        Assert.Contains(row.QuerySelectorAll("button.desklink"), l => l.GetAttribute("title") == "Open this event at SeatGeek");
        Assert.Contains("Day-of resale prices from SeatGeek.", cut.Markup);
    }

    [Fact]
    public void An_ungraded_row_says_why_instead_of_showing_a_saving()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        var row = RowFor(cut, "Upcoming Show");
        Assert.Contains("face value", row.TextContent);
        Assert.Equal("The event has not been played yet.", row.QuerySelector("[data-reason]")!.TextContent.Trim());
        Assert.Null(row.QuerySelector("[data-cell=saved-per]"));
    }

    [Fact]
    public void The_filter_narrows_to_graded_or_ungraded_rows()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        Assert.Equal(2, cut.FindAll("tbody .mud-table-row").Count);

        cut.FindAll(".gate__opt").Single(b => b.TextContent.Trim() == "Ungraded").Click();
        var rows = cut.FindAll("tbody .mud-table-row");
        Assert.Single(rows);
        Assert.Contains("Upcoming Show", rows[0].TextContent);

        cut.FindAll(".gate__opt").Single(b => b.TextContent.Trim() == "Graded").Click();
        Assert.Contains("Played Show", cut.Find("tbody .mud-table-row").TextContent);
    }

    [Fact]
    public void Delete_asks_first_as_a_destructive_alert_and_keeping_it_deletes_nothing()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        RowFor(cut, "Played Show").Click();
        var delete = cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Delete");
        Assert.Contains("desk-btn--destructive", delete.ClassList);

        Alerts.Answer = false;
        delete.Click();

        var asked = Assert.Single(Alerts.Asked);
        Assert.Equal("Delete this purchase?", asked.Heading);
        Assert.Equal("Delete purchase", asked.ConfirmLabel);
        Assert.Equal("Keep it", asked.CancelLabel);
        Assert.True(asked.Destructive);
        Assert.Contains("cannot be undone", asked.Message);
        Assert.Empty(Ledger.Deleted);
        Assert.Equal(2, cut.FindAll("tbody .mud-table-row").Count);
    }

    [Fact]
    public void Confirming_the_delete_removes_the_row()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        RowFor(cut, "Played Show").Click();
        Alerts.Answer = true;
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Delete").Click();

        Assert.Equal([1L], Ledger.Deleted);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody .mud-table-row")));
    }

    [Fact]
    public void Export_receipt_fetches_the_event_receipt_as_a_file()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        RowFor(cut, "Played Show").Click();
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Export receipt").Click();

        var open = Assert.Single(JSInterop.Invocations, i => i.Identifier == "open");
        Assert.Equal("/receipts/7.md", open.Arguments[0]);
    }

    [Fact]
    public void Open_event_opens_the_event_window_about_that_event()
    {
        var (graded, ungraded) = Two();
        var cut = Render(graded, ungraded);

        RowFor(cut, "Played Show").Click();
        cut.FindAll(".rowacts__keys button").Single(b => b.TextContent.Trim() == "Open event").Click();

        var window = Assert.Single(Windows.Windows, w => w.Definition.Key == WindowCatalog.Event);
        Assert.Equal(7, window.Parameters["EventId"]);
    }
}
