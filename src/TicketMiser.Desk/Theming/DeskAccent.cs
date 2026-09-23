namespace TicketMiser.Desk.Theming;

/// <summary>
/// The one accent, chosen.
///
/// <para>
/// The desk spends one hue on interactivity, focus and selection, and nothing else. Which
/// hue is not a design decision; it is the operator's, exactly as it is in macOS's own
/// appearance settings. Every position here is a hue the state colours do not use — there
/// is no red, orange or green, because a positive tag and a primary button that shared a
/// hue would stop meaning different things.
/// </para>
///
/// <para>
/// Each accent is five tokens in CSS (<c>--accent</c>, its hover and press, and the two
/// washes) redeclared under <c>[data-accent]</c> for both desks, and one <c>MudTheme</c>
/// primary in C#. The values are paired in <see cref="DeskAccents"/> and the pairing is
/// tested, for the same reason every other colour crosses the seam twice: MudBlazor
/// derives hover and darken shades from its palette, and a primary handed only to CSS
/// would hover to a blue that exists nowhere else.
/// </para>
/// </summary>
public enum DeskAccent
{
    /// <summary>systemBlue. The default, and the desk the product has always been.</summary>
    Blue,

    /// <summary>systemTeal.</summary>
    Teal,

    /// <summary>systemIndigo.</summary>
    Indigo,

    /// <summary>systemPurple.</summary>
    Purple,

    /// <summary>systemGray. Interactivity without a hue at all — macOS's graphite.</summary>
    Graphite
}

/// <summary>
/// One accent's five tokens on one desk. The literals are formatted exactly as the
/// stylesheet writes them so a diff between the two files reads as the same text.
/// </summary>
public sealed record AccentPalette(string Accent, string Hover, string Press, string Wash, string WashStrong);

/// <summary>
/// The accent table: every <see cref="DeskAccent"/> on both desks, plus its label.
/// </summary>
/// <remarks>
/// Hover travels the way the desk's ground demands, not the way the name suggests. On the
/// dark desk prominence is lightness, so hover lightens; against white a colour that
/// lightens under the pointer reads as fading, so on the light desk hover and press both
/// deepen. Washes drop from .15/.24 to .12/.20 on the light desk because a tint over white
/// composites to a far more saturated result than the same tint over near-black.
/// </remarks>
public static class DeskAccents
{
    public static readonly IReadOnlyList<DeskAccent> All =
        [DeskAccent.Blue, DeskAccent.Teal, DeskAccent.Indigo, DeskAccent.Purple, DeskAccent.Graphite];

    /// <summary>The attribute value stamped on <c>&lt;html&gt;</c>; matches the selectors in desk.css.</summary>
    public static string Key(DeskAccent accent) => accent.ToString().ToLowerInvariant();

    public static string Label(DeskAccent accent) => accent switch
    {
        DeskAccent.Blue => "Blue",
        DeskAccent.Teal => "Teal",
        DeskAccent.Indigo => "Indigo",
        DeskAccent.Purple => "Purple",
        DeskAccent.Graphite => "Graphite",
        _ => accent.ToString()
    };

    public static AccentPalette Dark(DeskAccent accent) => accent switch
    {
        DeskAccent.Teal => new("#40C8E0", "#5FD3E7", "#2BB7CF", "rgba(64, 200, 224, .15)", "rgba(64, 200, 224, .24)"),
        DeskAccent.Indigo => new("#5E5CE6", "#7C7AEB", "#4B49D9", "rgba(94, 92, 230, .15)", "rgba(94, 92, 230, .24)"),
        DeskAccent.Purple => new("#BF5AF2", "#CB78F5", "#AD3FE6", "rgba(191, 90, 242, .15)", "rgba(191, 90, 242, .24)"),
        DeskAccent.Graphite => new("#8E8E93", "#A1A1A6", "#7C7C81", "rgba(142, 142, 147, .15)", "rgba(142, 142, 147, .24)"),
        _ => new(DeskTheme.Accent, DeskTheme.AccentHover, "#0871DB", "rgba(10, 132, 255, .15)", "rgba(10, 132, 255, .24)")
    };

    public static AccentPalette Light(DeskAccent accent) => accent switch
    {
        DeskAccent.Teal => new("#30B0C7", "#2A9DB2", "#24899C", "rgba(48, 176, 199, .12)", "rgba(48, 176, 199, .20)"),
        DeskAccent.Indigo => new("#5856D6", "#4F4DC4", "#4644AD", "rgba(88, 86, 214, .12)", "rgba(88, 86, 214, .20)"),
        DeskAccent.Purple => new("#AF52DE", "#A046CF", "#8F3DB8", "rgba(175, 82, 222, .12)", "rgba(175, 82, 222, .20)"),
        DeskAccent.Graphite => new("#8E8E93", "#7D7D82", "#6C6C70", "rgba(142, 142, 147, .12)", "rgba(142, 142, 147, .20)"),
        _ => new(DeskTheme.LightAccent, DeskTheme.LightAccentHover, "#0062CC", "rgba(0, 122, 255, .12)", "rgba(0, 122, 255, .20)")
    };

    public static AccentPalette For(DeskAccent accent, bool isDark) => isDark ? Dark(accent) : Light(accent);
}
