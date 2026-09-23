using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The ledger's rules without a database: which day-of price a purchase is graded against, what
/// an ungraded purchase says about itself, what a savings summary adds up to — each fee basis
/// apart, never summed — and what a draft needs before it is a purchase.
/// </summary>
public class PurchaseLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };

    private static readonly IReadOnlyDictionary<int, Source> Sources = new Dictionary<int, Source> { [1] = Ticketmaster, [2] = SeatGeek };

    private static Event Played(int id = 7, string venue = "Ryman Auditorium") => new()
    {
        Id = id,
        Name = "Example Tour",
        StartsAt = Now.AddDays(-2),
        Venue = new Venue { Name = venue, City = "Nashville", State = "TN", Timezone = "America/Chicago" }
    };

    private static Purchase Bought(int eventId, int? sourceId, decimal paid, bool allIn, int quantity = 2, decimal? saved = null) => new()
    {
        Id = paid.GetHashCode(),
        EventId = eventId,
        SourceId = sourceId,
        Quantity = quantity,
        PaidPerTicket = paid,
        AllIn = allIn,
        PurchasedAt = Now.AddDays(-20),
        SavingsVsFinal = saved,
        ResolvedAt = saved is null ? null : Now.AddDays(-1)
    };

    private static FinalPrice Final(int eventId, Source source, decimal? lowest, bool allIn) => new()
    {
        EventId = eventId,
        SourceId = source.Id,
        Lowest = lowest,
        AllIn = allIn
    };

    [Fact]
    public void The_comparable_final_is_the_cheapest_priced_one_of_the_same_kind_in_the_same_market()
    {
        var purchase = Bought(7, SeatGeek.Id, 80m, allIn: true);
        var finals = new[]
        {
            Final(7, Ticketmaster, 50m, allIn: true),   // cheaper, but the other market
            Final(7, SeatGeek, 60m, allIn: false),      // same market, other fee basis
            Final(7, SeatGeek, null, allIn: true),      // no price: never the comparison
            Final(7, SeatGeek, 95m, allIn: true)
        };

        var chosen = PurchaseLedger.ComparableFinal(purchase, SourceKind.Resale, finals, Sources);

        Assert.NotNull(chosen);
        Assert.Equal(95m, chosen.Lowest);
    }

    [Fact]
    public void A_purchase_from_nowhere_tracked_grades_against_either_market_of_the_same_kind()
    {
        var purchase = Bought(7, null, 80m, allIn: true);
        var finals = new[] { Final(7, Ticketmaster, 70m, allIn: true), Final(7, SeatGeek, 95m, allIn: true) };

        Assert.Equal(70m, PurchaseLedger.ComparableFinal(purchase, null, finals, Sources)!.Lowest);
    }

    [Fact]
    public void A_graded_purchase_carries_its_saving_and_the_final_it_was_graded_against()
    {
        var evt = Played();
        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true, saved: 15m), evt,
            [Final(7, SeatGeek, 95m, allIn: true)], Sources, Now);

        Assert.Equal(PurchaseGrade.Graded, line.Grade);
        Assert.Null(line.Reason);
        Assert.Equal(95m, line.DayOf!.Lowest);
        Assert.Same(SeatGeek, line.DayOfSource);
        Assert.Equal(15m, line.SavedPerTicket);
        Assert.Equal(30m, line.SavedTotal);
        Assert.Equal(160m, line.Total);
    }

    [Fact]
    public void An_event_not_yet_played_is_ungraded_and_says_so()
    {
        var evt = Played();
        evt.StartsAt = Now.AddDays(5);

        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true), evt, [], Sources, Now);

        Assert.Equal(PurchaseGrade.NotYetPlayed, line.Grade);
        Assert.Equal("The event has not been played yet.", line.Reason);
        Assert.Null(line.SavedPerTicket);
    }

    [Fact]
    public void A_played_event_with_only_the_other_fee_basis_on_record_is_never_graded_across_it()
    {
        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true), Played(),
            [Final(7, SeatGeek, 60m, allIn: false)], Sources, Now);

        Assert.Equal(PurchaseGrade.NoComparableFinal, line.Grade);
        Assert.Null(line.DayOf);
        Assert.Contains("face value", line.Reason);
        Assert.Contains("all-in", line.Reason);
        Assert.Contains("not compared", line.Reason);
    }

    [Fact]
    public void A_played_event_with_no_final_at_all_says_nothing_was_recorded()
    {
        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true), Played(), [], Sources, Now);

        Assert.Equal(PurchaseGrade.NoComparableFinal, line.Grade);
        Assert.Equal("No day-of price was recorded for this event.", line.Reason);
    }

    [Fact]
    public void Only_the_other_market_on_record_names_the_market_the_purchase_was_in()
    {
        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true), Played(),
            [Final(7, Ticketmaster, 70m, allIn: true)], Sources, Now);

        Assert.Equal(PurchaseGrade.NoComparableFinal, line.Grade);
        Assert.Equal("No all-in day-of price was recorded in the resale market SeatGeek sells in.", line.Reason);
    }

    [Fact]
    public void A_comparable_final_not_yet_graded_waits_for_the_next_run()
    {
        var line = PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true), Played(),
            [Final(7, SeatGeek, 95m, allIn: true)], Sources, Now);

        Assert.Equal(PurchaseGrade.AwaitingGrade, line.Grade);
        Assert.Null(line.SavedPerTicket);
    }

    [Fact]
    public void The_summary_totals_each_fee_basis_apart_and_never_adds_a_face_saving_to_an_all_in_one()
    {
        var ryman = Played(7, "Ryman Auditorium");
        var bridgestone = Played(8, "Bridgestone Arena");
        var upcoming = Played(9, "Ryman Auditorium");
        upcoming.StartsAt = Now.AddDays(3);

        var lines = new[]
        {
            PurchaseLedger.Line(Bought(7, SeatGeek.Id, 80m, allIn: true, quantity: 2, saved: 15m), ryman, [Final(7, SeatGeek, 95m, true)], Sources, Now),
            PurchaseLedger.Line(Bought(8, Ticketmaster.Id, 50m, allIn: false, quantity: 1, saved: 5m), bridgestone, [Final(8, Ticketmaster, 55m, false)], Sources, Now),
            PurchaseLedger.Line(Bought(8, SeatGeek.Id, 70m, allIn: true, quantity: 1), bridgestone, [Final(8, Ticketmaster, 55m, false)], Sources, Now),
            PurchaseLedger.Line(Bought(9, null, 40m, allIn: true, quantity: 4), upcoming, [], Sources, Now)
        };

        var s = PurchaseLedger.Summarise(lines);

        Assert.Equal(3, s.AllIn.Purchases);
        Assert.Equal(160m + 70m + 160m, s.AllIn.Paid);
        Assert.Equal(1, s.AllIn.Graded);
        Assert.Equal(30m, s.AllIn.Saved);

        Assert.Equal(1, s.Face.Purchases);
        Assert.Equal(50m, s.Face.Paid);
        Assert.Equal(5m, s.Face.Saved);

        Assert.Equal(2, s.Graded);
        Assert.Equal(2, s.Ungraded);
        Assert.Equal(1, s.NotYetPlayed);
        Assert.Equal(1, s.NoComparableFinal);
        Assert.Equal(0, s.AwaitingGrade);

        // By source: SeatGeek's all-in rows, Ticketmaster's face row, and the untracked one — each
        // basis a row of its own, never folded into one number.
        var seatGeek = Assert.Single(s.BySource, g => g.Name == "SeatGeek");
        Assert.Equal(230m, seatGeek.AllIn.Paid);
        Assert.False(seatGeek.Face.Any);
        var ticketmaster = Assert.Single(s.BySource, g => g.Name == "Ticketmaster");
        Assert.Equal(5m, ticketmaster.Face.Saved);
        Assert.False(ticketmaster.AllIn.Any);
        Assert.Contains(s.BySource, g => g.Name == PurchaseLedger.Elsewhere);

        var bridgestoneGroup = Assert.Single(s.ByVenue, g => g.Name == "Bridgestone Arena");
        Assert.Equal(70m, bridgestoneGroup.AllIn.Paid);
        Assert.Equal(0m, bridgestoneGroup.AllIn.Saved);
        Assert.Equal(50m, bridgestoneGroup.Face.Paid);
        Assert.Equal(5m, bridgestoneGroup.Face.Saved);
    }

    [Fact]
    public void An_empty_ledger_summarises_to_nothing()
    {
        var s = PurchaseLedger.Summarise([]);

        Assert.Equal(0, s.Purchases);
        Assert.False(s.AllIn.Any);
        Assert.False(s.Face.Any);
        Assert.Empty(s.BySource);
    }

    [Theory]
    [InlineData(0, "quantity")]
    [InlineData(null, "quantity")]
    [InlineData(101, "quantity")]
    public void A_draft_needs_at_least_one_ticket(int? quantity, string field)
    {
        var errors = PurchaseLedger.Validate(new PurchaseDraft(7, quantity, 80m, true, null, null, null), Now);

        Assert.True(errors.ContainsKey(field));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_draft_needs_a_price_above_zero(int? price)
    {
        var errors = PurchaseLedger.Validate(new PurchaseDraft(7, 1, price, true, null, null, null), Now);

        Assert.Equal("The price per ticket, above zero.", errors["price"]);
    }

    [Fact]
    public void A_draft_must_say_whether_fees_are_included_and_there_is_no_default()
    {
        var errors = PurchaseLedger.Validate(new PurchaseDraft(7, 1, 80m, null, null, null, null), Now);

        Assert.Equal("Say whether the price includes fees.", errors["allIn"]);
        Assert.Throws<ArgumentException>(() => PurchaseLedger.ToPurchase(new PurchaseDraft(7, 1, 80m, null, null, null, null), Now));
    }

    [Fact]
    public void A_purchase_cannot_be_dated_in_the_future_and_the_note_has_a_limit()
    {
        var errors = PurchaseLedger.Validate(
            new PurchaseDraft(7, 1, 80m, true, null, Now.AddHours(2), new string('x', PurchaseLedger.NoteMaxLength + 1)), Now);

        Assert.Contains("purchasedAt", errors.Keys);
        Assert.Contains("note", errors.Keys);
    }

    [Fact]
    public void A_valid_draft_becomes_a_purchase_dated_now_when_no_date_was_given_and_needs_no_source()
    {
        var purchase = PurchaseLedger.ToPurchase(new PurchaseDraft(7, 2, 84.505m, false, null, null, "  Sec 102  "), Now);

        Assert.Equal(7, purchase.EventId);
        Assert.Null(purchase.SourceId);
        Assert.Equal(2, purchase.Quantity);
        Assert.Equal(84.51m, purchase.PaidPerTicket);
        Assert.False(purchase.AllIn);
        Assert.Equal(Now, purchase.PurchasedAt);
        Assert.Equal("Sec 102", purchase.Note);
        Assert.Null(purchase.SavingsVsFinal);
    }
}
