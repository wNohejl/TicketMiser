using System.Globalization;
using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// One source's latest word on one event: the newest row in the observation stream, or, when
/// the stream has nothing for that source, the newest tick of the on-sale record. The two are
/// the same reading with different lifetimes, and the board's comparator does not care which
/// table it was read from.
/// </summary>
/// <param name="AllIn">
/// Whether <paramref name="Lowest"/> includes fees. Null only when the reading came from a tick
/// whose source did not say; such a reading is never ranked, because a number of unknown kind
/// cannot be compared with anything.
/// </param>
/// <param name="FromOnSaleRecord">True when the reading is a tick rather than an observation.</param>
public sealed record SourceQuote(
    Source Source,
    decimal? Lowest,
    bool? AllIn,
    int? ListingCount,
    string Currency,
    DateTimeOffset ObservedAt,
    string? PrimaryStatus,
    bool FromOnSaleRecord)
{
    public SourceKind Kind => Source.Kind;
}

/// <summary>One source's mark on the spread rail.</summary>
/// <param name="Position">0 at the best price, 100 at the worst. Every tick sits at 0 when the set is not rankable.</param>
public sealed record RailTick(SourceQuote Quote, string Monogram, double Position, bool IsBest);

/// <summary>
/// The best number in one market for one event, and where every other source in that market
/// stands. Composed per market and never across them: a <see cref="SourceKind.Primary"/> summary
/// is built only from primary quotes and a <see cref="SourceKind.Resale"/> one only from resale
/// quotes, so no number here has ever been compared with the other market's.
/// </summary>
/// <param name="Best">The cheapest comparable quote, chosen by <see cref="PriceComparison.Cheapest"/>. Null with <paramref name="Reason"/> set.</param>
/// <param name="Reason">Why there is no best number, in the reader's terms. Null when <paramref name="Best"/> is set.</param>
/// <param name="Rail">One tick per source that reported, priced or not, best first.</param>
/// <param name="Spread">Worst minus best across the rankable quotes; null when fewer than two were ranked.</param>
public sealed record MarketBest(
    SourceKind Kind,
    SourceQuote? Best,
    string? Reason,
    IReadOnlyList<RailTick> Rail,
    decimal? Spread)
{
    /// <summary>Whether any source in this market has said anything at all.</summary>
    public bool Reported => Rail.Count > 0;

    /// <summary>
    /// Composes one market's summary. Quotes of another kind are dropped rather than ranked, so
    /// a caller that hands over the whole event by mistake still gets a per-market answer.
    /// </summary>
    public static MarketBest Compose(SourceKind kind, IEnumerable<SourceQuote> quotes)
    {
        if (kind == SourceKind.Feed)
            throw new ArgumentException("A feed names events; it never prices them.", nameof(kind));

        var inMarket = quotes
            .Where(q => q.Kind == kind)
            .OrderBy(q => q.Source.Name, StringComparer.Ordinal)
            .ToList();

        if (inMarket.Count == 0)
            return new MarketBest(kind, null, $"No {Word(kind)} source has reported.", [], null);

        var priced = inMarket.Where(q => q.Lowest is not null).ToList();

        if (priced.Count == 0)
        {
            var reason = inMarket.Count == 1
                ? $"{inMarket[0].Source.Name} reported without a price."
                : $"{inMarket.Count} sources reported, none with a price.";

            return new MarketBest(kind, null, reason, Unranked(inMarket), null);
        }

        // A reading whose fee basis is unstated is not a number of any kind, so it is neither
        // ranked nor allowed to block the ranking of the ones that are.
        var unstated = priced.Where(q => q.AllIn is null).ToList();
        var rankable = priced.Where(q => q.AllIn is not null).ToList();

        if (rankable.Count == 0)
        {
            var names = string.Join(" and ", unstated.Select(q => q.Source.Name));
            return new MarketBest(kind, null, $"{names} gave a price without saying whether fees are included.", Unranked(inMarket), null);
        }

        var best = PriceComparison.Cheapest(
            rankable.Select(q => (q.Source.Id, q.Kind, q.AllIn!.Value, q.Lowest)).ToList());

        if (best is null)
        {
            // The one refusal the comparator makes: the same market on two fee bases. Say which
            // sources are on which, because the fix is a fixture or a term, not a reload.
            var allIn = rankable.Where(q => q.AllIn == true).Select(q => q.Source.Name);
            var face = rankable.Where(q => q.AllIn == false).Select(q => q.Source.Name);

            return new MarketBest(
                kind,
                null,
                $"Not ranked: {string.Join(", ", allIn)} all-in against {string.Join(", ", face)} face value.",
                Unranked(inMarket),
                null);
        }

        var winner = rankable.Single(q => q.Source.Id == best.Value.SourceId);
        var low = best.Value.Lowest;
        var high = rankable.Max(q => q.Lowest!.Value);
        var span = high - low;

        // Ranked sources take their place on the rail; a source that reported without a price,
        // or without a fee basis, sits at the far end so the rail still has one tick per source.
        var rail = inMarket
            .Select(q =>
            {
                var ranked = q.AllIn is not null && q.Lowest is not null;
                var position = !ranked ? 100d
                    : span <= 0 ? 0d
                    : (double)((q.Lowest!.Value - low) / span * 100);

                return new RailTick(q, SourceMonogram.For(q.Source), position, q.Source.Id == winner.Source.Id);
            })
            .OrderBy(t => t.IsBest ? 0 : 1)
            .ThenBy(t => t.Position)
            .ToList();

        return new MarketBest(kind, winner, null, rail, rankable.Count > 1 ? span : null);
    }

    private static List<RailTick> Unranked(List<SourceQuote> quotes)
        => quotes.Select(q => new RailTick(q, SourceMonogram.For(q.Source), 0, false)).ToList();

    private static string Word(SourceKind kind) => kind == SourceKind.Primary ? "primary" : "resale";
}

/// <summary>
/// A source's two-letter mark. Hue means state on this desk and nothing else (ADR 0013), so a
/// source is identified typographically: TM beside a number rather than a red one.
/// </summary>
public static class SourceMonogram
{
    public static string For(Source source) => For(source.Key, source.Name);

    public static string For(string key, string name) => key switch
    {
        "ticketmaster" => "TM",
        "ticketmaster-resale" => "TR",
        "seatgeek" => "SG",
        "stubhub" => "SH",
        "axs" => "AX",
        _ => Initials(name.Length > 0 ? name : key)
    };

    private static string Initials(string text)
    {
        var words = text.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);

        var mark = words.Length >= 2
            ? $"{words[0][0]}{words[1][0]}"
            : text.Length >= 2 ? text[..2] : text;

        return mark.ToUpper(CultureInfo.InvariantCulture);
    }
}
