using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Log purchase follow-up: it starts from the price the row showed, never guesses the fee
/// basis from a face-value number, refuses a draft with what is wrong beside each field, logs a
/// valid one with a toast and resets, and says so when the event is not on record.
/// </summary>
public class LogPurchasePanelTests : PurchaseBench
{
    private static SourceQuote Quote(Source source, decimal lowest, bool? allIn)
        => new(source, lowest, allIn, null, "USD", Now.AddMinutes(-5), null, false);

    private static PurchaseForm Form(params SourceQuote[] quotes)
    {
        var evt = Ryman();
        return new PurchaseForm(evt, [Ticketmaster, SeatGeek],
            MarketBest.Compose(SourceKind.Primary, quotes), MarketBest.Compose(SourceKind.Resale, quotes));
    }

    private IRenderedComponent<LogPurchasePanel> Render(PurchaseForm? form, int eventId = 7)
    {
        UseLedger(new FakePurchaseQueries(form: form));
        return Render<LogPurchasePanel>(p => p.AddUnmatched("EventId", eventId));
    }

    private static string? Value(IRenderedComponent<LogPurchasePanel> cut, string field)
        => cut.Find($"[data-field={field}]").GetAttribute("value");

    private static void Save(IRenderedComponent<LogPurchasePanel> cut)
        => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Log purchase").Click();

    [Fact]
    public void Prefilled_from_a_face_value_number_the_fee_question_starts_unanswered()
    {
        var cut = Render(Form(Quote(Ticketmaster, 59.5m, allIn: false), Quote(SeatGeek, 84m, allIn: true)));

        Assert.Equal("1", Value(cut, "quantity"));
        Assert.Equal("59.5", Value(cut, "price"));
        Assert.Equal("1", Value(cut, "source"));
        Assert.DoesNotContain(cut.FindAll("[data-field=allIn] .gate__opt"), o => o.GetAttribute("aria-checked") == "true");
        Assert.Contains("Prefilled from a face-value number", cut.Markup);

        // Exactly one Filled key in the form.
        Assert.Single(cut.FindAll(".desk-btn--filled"));
    }

    [Fact]
    public void Saving_without_the_fee_basis_is_refused_beside_the_field_and_nothing_is_logged()
    {
        var cut = Render(Form(Quote(Ticketmaster, 59.5m, allIn: false)));

        Save(cut);

        Assert.Contains("Say whether the price includes fees.", cut.Markup);
        Assert.Empty(Ledger.Logged);
    }

    [Fact]
    public void A_bad_quantity_and_an_empty_price_are_refused_with_the_reason_under_each_field()
    {
        var cut = Render(Form());

        cut.Find("[data-field=quantity]").Change("0");
        cut.Find("[data-field=price]").Change("");
        cut.FindAll("[data-field=allIn] .gate__opt").Single(o => o.TextContent.Trim() == "All-in").Click();
        Save(cut);

        var errors = cut.FindAll(".field-err").Select(e => e.TextContent.Trim()).ToList();
        Assert.Contains("At least one ticket.", errors);
        Assert.Contains("The price per ticket, above zero.", errors);
        Assert.Equal("true", cut.Find("[data-field=quantity]").GetAttribute("aria-invalid"));
        Assert.Empty(Ledger.Logged);
    }

    [Fact]
    public void Starting_from_an_all_in_resale_price_answers_the_fee_question_and_a_save_logs_toasts_and_resets()
    {
        var cut = Render(Form(Quote(Ticketmaster, 59.5m, allIn: false), Quote(SeatGeek, 84m, allIn: true)));

        cut.FindAll("[data-prefill] button").Single(b => b.TextContent.Contains("Resale", StringComparison.Ordinal)).Click();
        Assert.Equal("84", Value(cut, "price"));
        Assert.Equal("2", Value(cut, "source"));
        Assert.Equal("true", cut.FindAll("[data-field=allIn] .gate__opt").Single(o => o.TextContent.Trim() == "All-in").GetAttribute("aria-checked"));

        cut.Find("[data-field=quantity]").Change("2");
        cut.Find("[data-field=note]").Change("Sec 4");
        Save(cut);

        var draft = Assert.Single(Ledger.Logged);
        Assert.Equal(7, draft.EventId);
        Assert.Equal(2, draft.Quantity);
        Assert.Equal(84m, draft.PaidPerTicket);
        Assert.True(draft.AllIn);
        Assert.Equal(2, draft.SourceId);
        Assert.Equal("Sec 4", draft.Note);
        Assert.NotNull(draft.PurchasedAt);

        var toast = Assert.Single(Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        Assert.Contains("Logged 2 tickets at $84.00 all-in for Example Tour.", toast.Message);

        // The form is back where it opened: the primary price, one ticket, no note.
        Assert.Equal("1", Value(cut, "quantity"));
        Assert.Equal("59.5", Value(cut, "price"));
        Assert.Empty(cut.Find("[data-field=note]").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void An_event_with_no_price_can_still_be_logged_elsewhere()
    {
        var cut = Render(Form());

        Assert.Contains("No source has a price for this event yet.", cut.Markup);
        Assert.Equal(string.Empty, Value(cut, "source"));

        cut.Find("[data-field=price]").Change("120");
        cut.FindAll("[data-field=allIn] .gate__opt").Single(o => o.TextContent.Trim() == "Face value").Click();
        Save(cut);

        var draft = Assert.Single(Ledger.Logged);
        Assert.Null(draft.SourceId);
        Assert.False(draft.AllIn);
        Assert.Equal(120m, draft.PaidPerTicket);
    }

    [Fact]
    public void An_unknown_event_says_so()
    {
        var cut = Render(null, eventId: 99);

        Assert.Contains("No event has id 99", cut.Find(".empty--error").TextContent);
    }
}
