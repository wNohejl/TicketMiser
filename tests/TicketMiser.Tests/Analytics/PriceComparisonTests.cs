using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The one rule every ranking obeys: a face-value number never sits in a ranking with an
/// all-in one, and a primary price never sits in a ranking with a resale one.
/// </summary>
public class PriceComparisonTests
{
    [Fact]
    public void Same_market_and_same_all_in_kind_are_comparable_and_nothing_else_is()
    {
        Assert.True(PriceComparison.Comparable(SourceKind.Resale, true, SourceKind.Resale, true));
        Assert.False(PriceComparison.Comparable(SourceKind.Resale, true, SourceKind.Resale, false));
        Assert.False(PriceComparison.Comparable(SourceKind.Primary, true, SourceKind.Resale, true));
        Assert.False(PriceComparison.Comparable(SourceKind.Feed, false, SourceKind.Feed, false));
    }

    [Fact]
    public void The_board_refuses_to_rank_a_mixed_set_rather_than_guess()
    {
        var mixed = new List<(int, SourceKind, bool, decimal?)>
        {
            (1, SourceKind.Primary, false, 59.5m),
            (2, SourceKind.Resale, false, 68m)
        };

        Assert.Null(PriceComparison.Cheapest(mixed));

        var resale = new List<(int, SourceKind, bool, decimal?)>
        {
            (2, SourceKind.Resale, false, 68m),
            (3, SourceKind.Resale, false, 64m),
            (4, SourceKind.Resale, false, null)
        };

        Assert.Equal((3, 64m), PriceComparison.Cheapest(resale));
    }

    [Fact]
    public void Savings_are_graded_only_against_the_same_kind_of_number()
    {
        Assert.Equal(12m, PriceComparison.SavingsVsFinal(paidPerTicket: 88m, paidAllIn: true, finalLowest: 100m, finalAllIn: true));
        Assert.Null(PriceComparison.SavingsVsFinal(88m, paidAllIn: true, finalLowest: 100m, finalAllIn: false));
        Assert.Null(PriceComparison.SavingsVsFinal(88m, true, null, true));
    }
}
