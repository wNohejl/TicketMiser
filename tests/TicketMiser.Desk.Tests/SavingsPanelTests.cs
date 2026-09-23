using Bunit;
using TicketMiser.Core.Analytics;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Savings window: an empty ledger says what it would total and offers Purchases; a ledger of
/// both fee bases totals them apart, says so, and never shows their sum; ungraded purchases are
/// counted under their reasons; the tables give each basis its own row.
/// </summary>
public class SavingsPanelTests : PurchaseBench
{
    private IRenderedComponent<SavingsPanel> Render(params PurchaseLine[] lines)
    {
        UseLedger(new FakePurchaseQueries(lines));
        return Render<SavingsPanel>();
    }

    private PurchaseLine[] Mixed()
    {
        var ryman = Ryman(7, "Ryman Night", daysFromNow: -3);
        var arena = Ryman(8, "Arena Night", daysFromNow: -2);
        arena.Venue!.Name = "Bridgestone Arena";
        var upcoming = Ryman(9, "Upcoming", daysFromNow: 10);

        return
        [
            // All-in on SeatGeek, saved $15 a ticket on two.
            Line(Bought(1, ryman, SeatGeek, 80m, allIn: true, quantity: 2, saved: 15m), ryman, Final(ryman, SeatGeek, 95m, true)),
            // Face value at Ticketmaster, saved $5 on one.
            Line(Bought(2, arena, Ticketmaster, 50m, allIn: false, quantity: 1, saved: 5m), arena, Final(arena, Ticketmaster, 55m, false)),
            // All-in on SeatGeek, but only a face-value final exists: never graded across bases.
            Line(Bought(3, arena, SeatGeek, 70m, allIn: true, quantity: 1), arena, Final(arena, Ticketmaster, 55m, false)),
            // Not played yet.
            Line(Bought(4, upcoming, null, 40m, allIn: true, quantity: 4), upcoming)
        ];
    }

    [Fact]
    public void An_empty_ledger_says_what_it_would_total_and_offers_Purchases()
    {
        var cut = Render();

        Assert.Contains("Nothing to total yet", cut.Find(".empty--new").TextContent);

        cut.Find(".empty__action button").Click();
        Assert.Single(Windows.Windows, w => w.Definition.Key == WindowCatalog.Purchases);
    }

    [Fact]
    public void All_in_and_face_value_savings_are_shown_apart_and_their_sum_appears_nowhere()
    {
        var cut = Render(Mixed());

        var allIn = cut.Find("[data-metric=saved-all-in]");
        var face = cut.Find("[data-metric=saved-face]");
        Assert.Contains("+$30.00", allIn.TextContent);
        Assert.Contains("Saved vs day-of, all-in", allIn.TextContent);
        Assert.Contains("+$5.00", face.TextContent);
        Assert.Contains("Saved vs day-of, face value", face.TextContent);

        Assert.Contains("$390.00", cut.Find("[data-metric=paid-all-in]").TextContent);
        Assert.Contains("$50.00", cut.Find("[data-metric=paid-face]").TextContent);

        // The refusal: no tile, no row and no note adds the two kinds together.
        Assert.DoesNotContain("$35.00", cut.Markup);
        Assert.DoesNotContain("$440.00", cut.Markup);
        var note = string.Join(' ', cut.Find("[data-kept-apart]").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("never added together", note);
    }

    [Fact]
    public void Ungraded_purchases_are_counted_under_their_reasons()
    {
        var cut = Render(Mixed());

        var graded = cut.Find("[data-metric=graded]");
        Assert.Contains("2", graded.QuerySelector(".metric__value")!.TextContent);
        Assert.Contains("of 4 purchases", graded.TextContent);

        var ungraded = cut.Find("[data-metric=ungraded]");
        Assert.Contains("2", ungraded.QuerySelector(".metric__value")!.TextContent);
        Assert.Contains("1 not played yet", ungraded.TextContent);
        Assert.Contains("1 with no day-of price of the same kind", ungraded.TextContent);
    }

    [Fact]
    public void The_source_and_venue_tables_give_each_basis_its_own_row()
    {
        var cut = Render(Mixed());

        var bySource = cut.Find("[data-table=by-source]");
        var seatGeek = bySource.QuerySelector("tr[data-group=SeatGeek][data-basis=all-in]")!;
        Assert.Contains("$230.00", seatGeek.TextContent);
        Assert.Contains("+$30.00", seatGeek.TextContent);
        Assert.Null(bySource.QuerySelector("tr[data-group=SeatGeek][data-basis=face]"));
        Assert.NotNull(bySource.QuerySelector("tr[data-group=Ticketmaster][data-basis=face]"));
        Assert.NotNull(bySource.QuerySelector($"tr[data-group={PurchaseLedger.Elsewhere}]"));

        var byVenue = cut.Find("[data-table=by-venue]");
        var arenaAllIn = byVenue.QuerySelector("tr[data-group='Bridgestone Arena'][data-basis=all-in]")!;
        var arenaFace = byVenue.QuerySelector("tr[data-group='Bridgestone Arena'][data-basis=face]")!;
        Assert.Contains("$70.00", arenaAllIn.TextContent);
        Assert.Contains("—", arenaAllIn.TextContent);
        Assert.Contains("+$5.00", arenaFace.TextContent);
        Assert.Contains("up", arenaFace.QuerySelector("td:last-child span")!.ClassList);
    }

    [Fact]
    public void A_ledger_of_one_basis_shows_only_that_basis_and_no_kept_apart_note()
    {
        var evt = Ryman(7, "Ryman Night", daysFromNow: -3);
        var cut = Render(Line(Bought(1, evt, SeatGeek, 80m, allIn: true, saved: -10m), evt, Final(evt, SeatGeek, 70m, true)));

        Assert.Contains("−$20.00", cut.Find("[data-metric=saved-all-in]").TextContent);
        Assert.Contains("metric--bad", cut.Find("[data-metric=saved-all-in]").ClassList);
        Assert.Empty(cut.FindAll("[data-metric=saved-face]"));
        Assert.Empty(cut.FindAll("[data-kept-apart]"));
    }
}
