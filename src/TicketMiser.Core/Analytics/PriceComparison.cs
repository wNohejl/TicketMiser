using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// The one rule every ranking on the desk obeys: two prices may be compared only when they are
/// the same kind of number. A face-value primary range against an all-in resale ask is not a
/// comparison, it is the deception the FTC sued over, and the board must never do it by
/// accident.
///
/// Kept in Core as a pure function so the board, the alert rules and the savings analytic all
/// call the same test, and a unit test can pin it without a database.
/// </summary>
public static class PriceComparison
{
    /// <summary>Whether two observations may sit in one ranking.</summary>
    public static bool Comparable(SourceKind aKind, bool aAllIn, SourceKind bKind, bool bAllIn)
        => aKind == bKind && aAllIn == bAllIn && aKind != SourceKind.Feed;

    /// <summary>
    /// The cheapest of a set within one market and one all-in kind. Null when the set is empty
    /// or mixes kinds, which is the caller's bug and is refused rather than papered over.
    /// </summary>
    public static (int SourceId, decimal Lowest)? Cheapest(
        IReadOnlyList<(int SourceId, SourceKind Kind, bool AllIn, decimal? Lowest)> observations)
    {
        var priced = observations.Where(o => o.Lowest is not null).ToList();
        if (priced.Count == 0)
            return null;

        var first = priced[0];
        if (priced.Any(o => !Comparable(first.Kind, first.AllIn, o.Kind, o.AllIn)))
            return null;

        var best = priced.MinBy(o => o.Lowest!.Value);
        return (best.SourceId, best.Lowest!.Value);
    }

    /// <summary>
    /// Paid versus the day-of price, per ticket. Positive means the buyer beat the day-of
    /// price. Null when the two numbers are not the same kind, because a fee-inclusive
    /// receipt against a face-value final would flatter every purchase by the fee.
    /// </summary>
    public static decimal? SavingsVsFinal(decimal paidPerTicket, bool paidAllIn, decimal? finalLowest, bool finalAllIn)
        => finalLowest is null || paidAllIn != finalAllIn ? null : finalLowest.Value - paidPerTicket;
}
