using TicketMiser.Desk.Theming;

namespace TicketMiser.Desk.Primitives;

/// <summary>
/// What a chart is plotting, stated as the kind of dataset rather than a list of colours.
///
/// <para>
/// This is <see cref="DeskState"/>'s rule applied to a chart. A panel names the family and
/// the palette follows, so the same kind of number is the same colour in every window and
/// nobody picks a hex at a call site. "Is this a Ledger?" is a reviewable question; "should
/// series two be orange?" is not.
/// </para>
///
/// <para>
/// The desk normally spends hue on state and nothing else. A multi-series chart cannot — it
/// needs hue to separate one line from another — so the families below are the sanctioned
/// exception, and each one still starts from the colour that already means what the chart is
/// about: the accent for something you interact with, the positive state for money you kept.
/// </para>
/// </summary>
public enum DeskChartFamily
{
    /// <summary>
    /// Prices and lines moving over time, one series per book/outcome. Hue is identity here,
    /// not state, so it opens on the accent (the desk's "this is the thing you are working")
    /// and walks the rest of the state colours before reaching for anything mixed.
    /// </summary>
    Movement,

    /// <summary>
    /// Money you have kept or lost — bankroll, cumulative CLV, running P/L. Positive first,
    /// because the desk already spells "won" in the positive state and "lost" in the negative.
    /// </summary>
    Ledger,

    /// <summary>
    /// How a source or job is behaving — success rates, latencies, budget burn. Ordered the
    /// way the pulse strip is: healthy, then warning, then breached.
    /// </summary>
    Health,

    /// <summary>
    /// Counts with no state in them — volume by book, entries by market. Deliberately quiet:
    /// chart neutrals, so a bar chart of "how many" cannot be misread as "how bad".
    /// </summary>
    Volume
}

/// <summary>
/// The palettes behind <see cref="DeskChartFamily"/>, in one place rather than at every call
/// site. Colours come from <see cref="DeskTheme"/> so the chart and the rest of the desk move
/// together; a chart cannot read <c>var(--accent)</c> because MudBlazor hands these values to
/// SVG attributes and to its own legend markup, which need resolved colours.
/// </summary>
public static class DeskChartPalette
{
    /// <summary>
    /// Cycled once the named colours run out — the desk names four colours, a game has more
    /// series than that.
    /// Opaque on purpose: these reach MudBlazor's SVG renderer directly, and a
    /// translucent colour would composite differently over gridlines and overlapping
    /// series instead of staying one fixed hue.
    /// </summary>
    private const string ChartNeutral = DeskTheme.ChartNeutral;
    private const string ChartNeutralDim = DeskTheme.ChartNeutralDim;

    /// <summary>The colour order for a family on the dark blue desk. A fresh array each call.</summary>
    public static string[] For(DeskChartFamily family) => For(family, isDark: true);

    /// <summary>The colour order for a family on the desk that is currently showing, in the default blue.</summary>
    public static string[] For(DeskChartFamily family, bool isDark) => For(family, isDark, DeskAccent.Blue);

    /// <summary>
    /// The colour order for a family on the desk that is currently showing, carrying the
    /// accent the operator chose.
    /// </summary>
    /// <remarks>
    /// The theme and accent are parameters rather than something this class reads, because
    /// it is static and both are per-circuit. The shorter overloads are kept, and kept
    /// meaning the dark blue desk, so a call site that has neither to hand degrades to the
    /// desk the product already was rather than to a compile error.
    ///
    /// <para>
    /// This is the one corner of the desk a token block cannot reach: MudChart writes these
    /// into SVG attributes and legend markup, where a <c>var()</c> does not resolve. So a
    /// chart is the single place where the second theme costs a second array rather than
    /// nothing — and exactly the place where forgetting would go unnoticed longest, because
    /// the dark series colours are saturated enough to still look deliberate on white. The
    /// dim neutral is what gives it away: <c>#636366</c> on a pale plot is not a quiet
    /// series, it is the darkest thing in the window.
    /// </para>
    /// </remarks>
    public static string[] For(DeskChartFamily family, bool isDark, DeskAccent accent)
    {
        var acc = DeskAccents.For(accent, isDark).Accent;
        var positive = isDark ? DeskTheme.StatePositive : DeskTheme.LightStatePositive;
        var negative = isDark ? DeskTheme.StateNegative : DeskTheme.LightStateNegative;
        var warning = isDark ? DeskTheme.StateWarning : DeskTheme.LightStateWarning;
        var neutral = isDark ? ChartNeutral : DeskTheme.LightChartNeutral;
        var neutralDim = isDark ? ChartNeutralDim : DeskTheme.LightChartNeutralDim;

        return family switch
        {
            DeskChartFamily.Ledger => [positive, negative, acc, warning, neutral, neutralDim],
            DeskChartFamily.Health => [positive, warning, negative, acc, neutral, neutralDim],
            DeskChartFamily.Volume => [neutral, acc, neutralDim, positive, warning, negative],
            _ => [acc, positive, warning, negative, neutral, neutralDim]
        };
    }
}

/// <summary>
/// The shapes a desk chart comes in. A curated subset of MudBlazor's <c>ChartType</c>: the
/// ones the desk has a reason for, named the same way so the mapping stays obvious.
/// </summary>
public enum DeskChartKind
{
    /// <summary>A quantity over time. The default, and what every chart on the desk is today.</summary>
    Line,

    /// <summary>A quantity across categories — by book, by market.</summary>
    Bar,

    /// <summary>Bars sharing a track, when the categories add up to something.</summary>
    StackedBar,

    /// <summary>Parts of one whole, when the parts are few and the whole is the point.</summary>
    Donut
}
