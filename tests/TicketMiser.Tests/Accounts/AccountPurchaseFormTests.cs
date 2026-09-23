using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using TicketMiser.Web.Accounts;

namespace TicketMiser.Tests.Accounts;

/// <summary>
/// What POST /account/purchases makes of a form: the ledger's own validation, plus what text can
/// get wrong before the ledger sees it. The fee basis has no default; a source the form did not
/// offer, or a date that is not one, is refused rather than guessed.
/// </summary>
public class AccountPurchaseFormTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);

    private static readonly int[] Offered = [1, 2];

    private static PurchaseFormInput Input(string? quantity = "2", string? price = "84.50", string? allIn = "true",
        string? sourceId = "", string? purchasedAt = "", string? note = "")
        => new(7, quantity, price, allIn, sourceId, purchasedAt, note);

    [Fact]
    public void A_complete_form_is_a_draft_with_no_errors()
    {
        var (draft, errors) = AccountPurchaseForm.Read(Input(sourceId: "2", note: "floor, row C"), Offered, Now);

        Assert.Empty(errors);
        Assert.Equal(7, draft.EventId);
        Assert.Equal(2, draft.Quantity);
        Assert.Equal(84.50m, draft.PaidPerTicket);
        Assert.True(draft.AllIn);
        Assert.Equal(2, draft.SourceId);
        Assert.Null(draft.PurchasedAt);
        Assert.Equal("floor, row C", draft.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void An_unanswered_fee_basis_is_refused_never_defaulted(string? allIn)
    {
        var (draft, errors) = AccountPurchaseForm.Read(Input(allIn: allIn), Offered, Now);

        Assert.Null(draft.AllIn);
        Assert.Equal(["allIn"], errors.Keys);
    }

    [Fact]
    public void Face_value_is_an_answer()
    {
        var (draft, errors) = AccountPurchaseForm.Read(Input(allIn: "false"), Offered, Now);

        Assert.Empty(errors);
        Assert.False(draft.AllIn);
    }

    [Theory]
    [InlineData("$84.50", 84.50)]
    [InlineData(" 1,200 ", 1200)]
    public void A_price_is_read_the_way_a_receipt_prints_it(string typed, double expected)
    {
        var (draft, errors) = AccountPurchaseForm.Read(Input(price: typed), Offered, Now);

        Assert.Empty(errors);
        Assert.Equal((decimal)expected, draft.PaidPerTicket);
    }

    [Theory]
    [InlineData("0", "84.50", "quantity")]
    [InlineData("101", "84.50", "quantity")]
    [InlineData("two", "84.50", "quantity")]
    [InlineData("2", "abc", "price")]
    [InlineData("2", "-5", "price")]
    [InlineData("2", "0", "price")]
    public void The_ledger_s_own_rules_decide_quantity_and_price(string quantity, string price, string field)
    {
        var (_, errors) = AccountPurchaseForm.Read(Input(quantity: quantity, price: price), Offered, Now);

        Assert.Equal([field], errors.Keys);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("x")]
    public void A_source_the_form_did_not_offer_is_refused(string sourceId)
    {
        var (draft, errors) = AccountPurchaseForm.Read(Input(sourceId: sourceId), Offered, Now);

        Assert.Null(draft.SourceId);
        Assert.Equal(AccountPurchaseForm.UnknownSource, Assert.Single(errors).Value);
    }

    [Fact]
    public void A_time_is_read_in_Nashville()
    {
        // 10:30 on 4 October is Central Daylight Time, five hours behind UTC.
        var (draft, errors) = AccountPurchaseForm.Read(Input(purchasedAt: "2026-10-04T10:30"), Offered, Now);

        Assert.Empty(errors);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 15, 30, 0, TimeSpan.Zero), draft.PurchasedAt!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("yesterday", "A date and time, or leave it empty for now.")]
    [InlineData("2026-10-06T10:30", "A purchase cannot be in the future.")]
    public void A_time_that_is_not_one_or_not_yet_is_refused(string typed, string message)
    {
        var (_, errors) = AccountPurchaseForm.Read(Input(purchasedAt: typed), Offered, Now);

        Assert.Equal(message, errors["purchasedAt"]);
    }

    [Fact]
    public void A_note_longer_than_the_ledger_keeps_is_refused()
    {
        var (_, errors) = AccountPurchaseForm.Read(Input(note: new string('n', 1025)), Offered, Now);

        Assert.Equal(["note"], errors.Keys);
    }

    [Fact]
    public void The_posted_fields_are_read_by_name_and_kept_as_typed()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["eventId"] = "7",
            ["quantity"] = "2",
            ["price"] = "abc",
            ["sourceId"] = "2",
            ["note"] = "floor"
        });

        var input = PurchaseFormInput.From(form);

        Assert.Equal(new PurchaseFormInput(7, "2", "abc", "", "2", "", "floor"), input);
    }
}
