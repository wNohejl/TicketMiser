using System.Globalization;
using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>Where a purchase stands against the day-of price.</summary>
public enum PurchaseGrade
{
    /// <summary>Graded: <see cref="Purchase.SavingsVsFinal"/> is set against a final of the same all-in kind.</summary>
    Graded,

    /// <summary>The event has not started, so there is no day-of price yet.</summary>
    NotYetPlayed,

    /// <summary>A comparable final exists and the next retention run will grade against it.</summary>
    AwaitingGrade,

    /// <summary>
    /// The event has been played and no final price of the same all-in kind (and market, when the
    /// source is known) was recorded. Never graded against the other kind: a fee-inclusive receipt
    /// against a face-value final flatters every purchase by the fee.
    /// </summary>
    NoComparableFinal
}

/// <summary>
/// One logged purchase as the Purchases window reads it: the row, what it was bought for, the
/// day-of price it was graded against when it was, and why not when it was not.
/// </summary>
/// <param name="DayOf">The final the grade used, or would use; null when there is none of the same kind.</param>
/// <param name="DayOfSource">The source of <paramref name="DayOf"/>.</param>
public sealed record PurchaseLine(
    Purchase Purchase,
    Event Event,
    Source? Source,
    FinalPrice? DayOf,
    Source? DayOfSource,
    PurchaseGrade Grade,
    string? Reason)
{
    public decimal Total => Purchase.PaidPerTicket * Purchase.Quantity;

    /// <summary>Saved per ticket; set only when graded.</summary>
    public decimal? SavedPerTicket => Grade == PurchaseGrade.Graded ? Purchase.SavingsVsFinal : null;

    public decimal? SavedTotal => SavedPerTicket * Purchase.Quantity;
}

/// <summary>What one fee basis adds up to. The two bases are totalled apart and never summed.</summary>
/// <param name="Paid">Every purchase on this basis, graded or not.</param>
/// <param name="Saved">Graded purchases only: savings per ticket times quantity.</param>
public sealed record BasisTotals(bool AllIn, int Purchases, int Tickets, decimal Paid, int Graded, decimal Saved)
{
    public static BasisTotals Empty(bool allIn) => new(allIn, 0, 0, 0m, 0, 0m);

    public bool Any => Purchases > 0;
}

/// <summary>One source's or one venue's totals, each basis kept to itself.</summary>
public sealed record SavingsGroup(string Name, BasisTotals AllIn, BasisTotals Face);

/// <summary>
/// The Savings window's numbers. All-in and face-value purchases are totalled apart: a saving
/// measured against a fee-inclusive day-of price and one measured against a face-value price are
/// not the same kind of number, and adding them would state a sum nobody paid or saved.
/// </summary>
public sealed record SavingsSummary(
    BasisTotals AllIn,
    BasisTotals Face,
    int NotYetPlayed,
    int AwaitingGrade,
    int NoComparableFinal,
    IReadOnlyList<SavingsGroup> BySource,
    IReadOnlyList<SavingsGroup> ByVenue)
{
    public int Purchases => AllIn.Purchases + Face.Purchases;

    public int Graded => AllIn.Graded + Face.Graded;

    public int Ungraded => NotYetPlayed + AwaitingGrade + NoComparableFinal;
}

/// <summary>What the Log purchase form hands the service. Nullable where the form may leave a field empty.</summary>
public sealed record PurchaseDraft(
    int EventId,
    int? Quantity,
    decimal? PaidPerTicket,
    bool? AllIn,
    int? SourceId,
    DateTimeOffset? PurchasedAt,
    string? Note);

/// <summary>
/// The purchase ledger's rules, pure so they can be pinned without a database: which day-of price
/// a purchase is graded against, what state an ungraded one is in and why, what a savings summary
/// adds up to, and what makes a draft a purchase.
/// </summary>
public static class PurchaseLedger
{
    public const int NoteMaxLength = 1024;

    /// <summary>The label for a purchase made somewhere no source covers.</summary>
    public const string Elsewhere = "Elsewhere";

    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// The final a purchase is graded against: the cheapest priced final of the same all-in kind,
    /// in the same market when the purchase names its source. The rule the retention run grades
    /// by, and the one the window shows, so the two cannot disagree.
    /// </summary>
    public static FinalPrice? ComparableFinal(Purchase purchase, SourceKind? purchaseKind, IEnumerable<FinalPrice> finals, IReadOnlyDictionary<int, Source> sources)
        => finals
            .Where(f => f.EventId == purchase.EventId && f.Lowest is not null && f.AllIn == purchase.AllIn)
            .Where(f => purchaseKind is null || (sources.TryGetValue(f.SourceId, out var s) && s.Kind == purchaseKind))
            .OrderBy(f => f.Lowest)
            .FirstOrDefault();

    /// <summary>One purchase, placed against the day-of price or told why it is not.</summary>
    public static PurchaseLine Line(
        Purchase purchase,
        Event evt,
        IEnumerable<FinalPrice> finals,
        IReadOnlyDictionary<int, Source> sources,
        DateTimeOffset now)
    {
        var source = purchase.SourceId is { } sid ? sources.GetValueOrDefault(sid) : null;
        var finalList = finals.Where(f => f.EventId == purchase.EventId).ToList();
        var dayOf = ComparableFinal(purchase, source?.Kind, finalList, sources);
        var dayOfSource = dayOf is null ? null : sources.GetValueOrDefault(dayOf.SourceId);

        if (purchase.SavingsVsFinal is not null)
            return new PurchaseLine(purchase, evt, source, dayOf, dayOfSource, PurchaseGrade.Graded, null);

        if (evt.StartsAt > now)
            return new PurchaseLine(purchase, evt, source, null, null, PurchaseGrade.NotYetPlayed, "The event has not been played yet.");

        if (dayOf is not null)
            return new PurchaseLine(purchase, evt, source, dayOf, dayOfSource, PurchaseGrade.AwaitingGrade,
                "A day-of price is on record; the next retention run grades it.");

        return new PurchaseLine(purchase, evt, source, null, null, PurchaseGrade.NoComparableFinal,
            NoComparableReason(purchase, source, finalList));
    }

    /// <summary>
    /// Why a played event's purchase has no grade, in the reader's terms: nothing was recorded, or
    /// only the other fee basis was, or only the other market was.
    /// </summary>
    public static string NoComparableReason(Purchase purchase, Source? source, IReadOnlyList<FinalPrice> finals)
    {
        var priced = finals.Where(f => f.Lowest is not null).ToList();
        var basis = BasisWord(purchase.AllIn);

        if (priced.Count == 0)
            return "No day-of price was recorded for this event.";

        if (priced.All(f => f.AllIn != purchase.AllIn))
            return $"The day-of prices on record are {BasisWord(!purchase.AllIn)}; this purchase is {basis}, and the two are not compared.";

        return source is null
            ? $"No {basis} day-of price was recorded."
            : $"No {basis} day-of price was recorded in the {MarketWord(source.Kind)} market {source.Name} sells in.";
    }

    /// <summary>
    /// The Savings window's summary. Only <see cref="PurchaseGrade.Graded"/> lines contribute a
    /// saving; each basis is totalled apart; every ungraded line is counted under its reason.
    /// </summary>
    public static SavingsSummary Summarise(IEnumerable<PurchaseLine> lines)
    {
        var list = lines.ToList();

        return new SavingsSummary(
            Totals(list, allIn: true),
            Totals(list, allIn: false),
            list.Count(l => l.Grade == PurchaseGrade.NotYetPlayed),
            list.Count(l => l.Grade == PurchaseGrade.AwaitingGrade),
            list.Count(l => l.Grade == PurchaseGrade.NoComparableFinal),
            Groups(list, l => l.Source?.Name ?? Elsewhere),
            Groups(list, l => l.Event.Venue?.Name ?? "Unknown venue"));
    }

    private static IReadOnlyList<SavingsGroup> Groups(List<PurchaseLine> lines, Func<PurchaseLine, string> key)
        => lines
            .GroupBy(key, StringComparer.Ordinal)
            .Select(g => new SavingsGroup(g.Key, Totals(g, allIn: true), Totals(g, allIn: false)))
            .OrderByDescending(g => g.AllIn.Paid + g.Face.Paid)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

    private static BasisTotals Totals(IEnumerable<PurchaseLine> lines, bool allIn)
    {
        var mine = lines.Where(l => l.Purchase.AllIn == allIn).ToList();
        var graded = mine.Where(l => l.Grade == PurchaseGrade.Graded).ToList();

        return new BasisTotals(
            allIn,
            mine.Count,
            mine.Sum(l => l.Purchase.Quantity),
            mine.Sum(l => l.Total),
            graded.Count,
            graded.Sum(l => l.SavedTotal ?? 0m));
    }

    /// <summary>
    /// What is wrong with a draft, keyed by field: <c>quantity</c>, <c>price</c>, <c>allIn</c>,
    /// <c>purchasedAt</c>, <c>note</c>. Empty when the draft is a purchase. The fee basis has no
    /// default: a number typed from a face-value price and a receipt total look the same.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Validate(PurchaseDraft draft, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (draft.Quantity is not { } quantity || quantity < 1)
            errors["quantity"] = "At least one ticket.";
        else if (quantity > 100)
            errors["quantity"] = "At most 100 tickets on one line.";

        if (draft.PaidPerTicket is not { } price || price <= 0)
            errors["price"] = "The price per ticket, above zero.";
        else if (price >= 1_000_000m)
            errors["price"] = "That is not a ticket price.";

        if (draft.AllIn is null)
            errors["allIn"] = "Say whether the price includes fees.";

        // A purchase is something that happened. A minute of grace covers a clock a little ahead.
        if (draft.PurchasedAt is { } at && at > now.AddMinutes(1))
            errors["purchasedAt"] = "A purchase cannot be in the future.";

        if (draft.Note is { Length: > NoteMaxLength })
            errors["note"] = $"At most {NoteMaxLength.ToString(CultureInfo.InvariantCulture)} characters.";

        return errors;
    }

    /// <summary>The purchase a valid draft becomes. Throws when the draft is not valid; call <see cref="Validate"/> first.</summary>
    public static Purchase ToPurchase(PurchaseDraft draft, DateTimeOffset now)
    {
        var errors = Validate(draft, now);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors.Values), nameof(draft));

        return new Purchase
        {
            EventId = draft.EventId,
            SourceId = draft.SourceId,
            Quantity = draft.Quantity!.Value,
            PaidPerTicket = decimal.Round(draft.PaidPerTicket!.Value, 2, MidpointRounding.AwayFromZero),
            AllIn = draft.AllIn!.Value,
            Currency = "USD",
            PurchasedAt = draft.PurchasedAt ?? now,
            Note = string.IsNullOrWhiteSpace(draft.Note) ? null : draft.Note.Trim()
        };
    }

    public static string BasisWord(bool allIn) => allIn ? "all-in" : "face value";

    public static string MarketWord(SourceKind kind) => kind switch
    {
        SourceKind.Primary => "primary",
        SourceKind.Resale => "resale",
        _ => "feed"
    };

    public static string Amount(decimal amount, string currency = "USD")
        => currency == "USD" ? amount.ToString("C2", Money) : $"{amount.ToString("N2", Money)} {currency}";
}
