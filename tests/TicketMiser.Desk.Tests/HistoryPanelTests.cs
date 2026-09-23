using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The History window: the final prices the record holds, and an honest account of why a
/// past event that was not watched has none.
/// </summary>
public class HistoryPanelTests : OperationsPanelBench
{
    [Fact]
    public void With_no_final_prices_it_says_backfill_is_not_on_offer()
    {
        Services.AddSingleton<IHistoryQueries>(new FakeHistoryQueries());

        var cut = Render<HistoryPanel>();

        var empty = cut.Find(".empty--new").TextContent;

        Assert.Contains("No final prices yet", empty);
        Assert.Contains("No current source offers a backfill", empty);
    }

    [Fact]
    public void A_final_price_row_names_its_source_and_whether_the_number_is_all_in()
    {
        var source = Ticketmaster();
        var evt = new Event { Id = 5, Name = "The Killers", StartsAt = DateTimeOffset.UtcNow.AddDays(-1), Venue = new Venue { Name = "Bridgestone Arena" } };

        var final = new FinalPrice
        {
            Id = 1,
            EventId = 5,
            Event = evt,
            SourceId = 1,
            Source = source,
            Lowest = 89,
            Highest = 250,
            AllIn = true,
            ObservedAt = evt.StartsAt.AddMinutes(-30),
            PromotedAt = evt.StartsAt.AddMinutes(10)
        };

        Services.AddSingleton<IHistoryQueries>(new FakeHistoryQueries(new HistorySummary(3, 1, 1), [final]));

        var cut = Render<HistoryPanel>();

        Assert.Empty(cut.FindAll(".empty--new"));
        Assert.Contains("The Killers", cut.Markup);
        Assert.Contains("Bridgestone Arena", cut.Markup);
        Assert.Contains("All-in", cut.FindAll(".tag").Select(t => t.TextContent.Trim()));
    }
}
