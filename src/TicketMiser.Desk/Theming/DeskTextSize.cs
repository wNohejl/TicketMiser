namespace TicketMiser.Desk.Theming;

/// <summary>
/// How large the type is, independent of how large the desk is.
///
/// <para>
/// Two settings because they answer two questions. Text size is legibility: the ramp
/// grows and every control's line box grows with it, but the spacing, the radii and the
/// title bars stay where they are, so a larger reader gets larger words in the same desk.
/// <see cref="DeskUiScale"/> is density: the whole desk, spacing included, drawn larger or
/// smaller. macOS separates the two for the same reason, and an operator on a 4K panel who
/// wants more windows wants the second, not a smaller font.
/// </para>
///
/// <para>
/// The multiplier lands on <c>--type-scale</c> and every <c>font-size</c> in desk.css and
/// mud-bridge.css is written against it. The range is deliberately narrow: past 1.25 the
/// fixed line boxes — a 28px button, a 34px title bar — start clipping, and that is what
/// UI scale is for.
/// </para>
/// </summary>
public enum DeskTextSize
{
    Small,
    Default,
    Large,
    ExtraLarge
}

public static class DeskTextSizes
{
    public static readonly IReadOnlyList<DeskTextSize> All =
        [DeskTextSize.Small, DeskTextSize.Default, DeskTextSize.Large, DeskTextSize.ExtraLarge];

    /// <summary>The multiplier written to <c>--type-scale</c>.</summary>
    public static double Scale(DeskTextSize size) => size switch
    {
        DeskTextSize.Small => 0.90,
        DeskTextSize.Large => 1.12,
        DeskTextSize.ExtraLarge => 1.25,
        _ => 1.0
    };

    public static string Label(DeskTextSize size) => size switch
    {
        DeskTextSize.Small => "Small",
        DeskTextSize.Large => "Large",
        DeskTextSize.ExtraLarge => "Larger",
        _ => "Default"
    };

    /// <summary>What body text measures at this size, for the sentence under the control.</summary>
    public static double BodyPx(DeskTextSize size) => Math.Round(13 * Scale(size), 1);
}

/// <summary>
/// The whole desk, drawn at a percentage of itself.
///
/// <para>
/// Applied as CSS <c>zoom</c> on <c>&lt;body&gt;</c>, which is the one mechanism that
/// scales spacing, radii, shadows and type together without a second copy of every token.
/// The windowing script measures the desk in viewport pixels and lays windows out in CSS
/// pixels, so it divides by the live zoom wherever the two meet; see windowing.js.
/// </para>
/// </summary>
public static class DeskUiScale
{
    /// <summary>The percentages the gate offers. Five, because the gate holds five honestly.</summary>
    public static readonly IReadOnlyList<int> Steps = [75, 90, 100, 110, 125];

    public const int Default = 100;

    /// <summary>Snaps any stored or typed value onto the nearest step, so a stale value cannot draw a desk at 3%.</summary>
    public static int Clamp(int percent) => Steps.MinBy(step => Math.Abs(step - percent));

    /// <summary>The factor written to <c>--ui-scale</c>.</summary>
    public static double Factor(int percent) => Clamp(percent) / 100d;
}
