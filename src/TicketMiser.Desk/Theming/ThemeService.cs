using Microsoft.JSInterop;
using MudBlazor;

namespace TicketMiser.Desk.Theming;

/// <summary>
/// How the desk looks, and the one place that decides it.
///
/// <para>
/// Four settings: which desk (<see cref="Mode"/>), which accent, how large the type, and
/// how large everything. Scoped, for the same reason <c>WindowManager</c> and
/// <c>DeskToasts</c> are: the DOM this writes to and the <c>localStorage</c> it reads from
/// belong to one circuit, and a singleton would hand every operator on the server the last
/// one's choice.
/// </para>
///
/// <para>
/// The two consumers want different things and both are served from here. The desk's own
/// components read the attributes and custom properties this stamps onto <c>&lt;html&gt;</c>
/// — <c>data-theme</c> switches the token block, <c>data-accent</c> the accent block,
/// <c>--type-scale</c> and <c>--ui-scale</c> the ramp and the zoom — and that is the whole
/// mechanism. MudBlazor cannot read a token block, so <c>MudThemeProvider</c> takes
/// <see cref="IsDark"/> and <see cref="MudTheme"/> from the same fields; the two are never
/// computed separately, because a desk whose Mud components disagree with its own panels is
/// worse than either theme alone.
/// </para>
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    private const string ModulePath = "./_content/TicketMiser.Desk/js/theme.js";

    /// <summary>The value stamped on <c>&lt;html&gt;</c>. Must match the selectors in desk.css.</summary>
    internal const string DarkTheme = "apple-dark";

    internal const string LightTheme = "light";

    // The localStorage keys, one per setting, so a value the product has stopped writing
    // is simply never read rather than breaking the parse of the ones it still does.
    public const string ModeKey = "ticketmiser.theme";
    public const string AccentKey = "ticketmiser.accent";
    public const string TextSizeKey = "ticketmiser.text";
    public const string UiScaleKey = "ticketmiser.scale";

    private readonly IJSRuntime _js;

    private IJSObjectReference? _module;
    private DotNetObjectReference<ThemeService>? _self;

    /// <summary>
    /// What the machine last said. Seeded on the first render and kept current by the
    /// matchMedia listener, so <see cref="DeskThemeMode.System"/> stays a live answer.
    /// </summary>
    private bool _systemPrefersDark = true;

    public ThemeService(IJSRuntime js) => _js = js;

    /// <summary>The operator's choice — the setting, not the outcome.</summary>
    public DeskThemeMode Mode { get; private set; } = DeskThemeMode.System;

    public DeskAccent Accent { get; private set; } = DeskAccent.Blue;

    public DeskTextSize TextSize { get; private set; } = DeskTextSize.Default;

    /// <summary>Whole desk scale, as a percentage on one of <see cref="DeskUiScale.Steps"/>.</summary>
    public int UiScale { get; private set; } = DeskUiScale.Default;

    /// <summary>
    /// The outcome — the setting resolved against the machine. This is what both
    /// <c>MudThemeProvider</c> and the <c>[data-theme]</c> attribute are driven from.
    /// </summary>
    public bool IsDark => Mode switch
    {
        DeskThemeMode.Dark => true,
        DeskThemeMode.Light => false,
        _ => _systemPrefersDark
    };

    /// <summary>The theme MudBlazor is handed: the desk's palettes carrying the chosen accent.</summary>
    public MudTheme MudTheme => DeskTheme.For(Accent);

    /// <summary>
    /// Raised whenever anything visible may have moved. The layout re-renders on it,
    /// because <c>MudThemeProvider</c> takes its mode and theme as parameters rather than
    /// reading them back, and nothing else would tell it to look again.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// Reads the stored choices and the machine's preference, then paints.
    ///
    /// <para>
    /// Called from the layout's first <c>OnAfterRenderAsync</c> and nowhere else. It
    /// cannot run earlier: there is no JS runtime during prerender, so a constructor or
    /// <c>OnInitializedAsync</c> that reached for <c>localStorage</c> would throw on
    /// every first paint. The cost is one frame of the defaults before the stored
    /// ones land, which is why the defaults are the dark, blue, unscaled desk the product
    /// already was — an operator who has never chosen sees no flash at all.
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        var module = await ModuleAsync();

        _systemPrefersDark = await module.InvokeAsync<bool>("prefersDark");

        if (Enum.TryParse<DeskThemeMode>(await module.InvokeAsync<string?>("read", ModeKey), ignoreCase: true, out var mode))
            Mode = mode;

        if (Enum.TryParse<DeskAccent>(await module.InvokeAsync<string?>("read", AccentKey), ignoreCase: true, out var accent))
            Accent = accent;

        if (Enum.TryParse<DeskTextSize>(await module.InvokeAsync<string?>("read", TextSizeKey), ignoreCase: true, out var size))
            TextSize = size;

        if (int.TryParse(await module.InvokeAsync<string?>("read", UiScaleKey), out var scale))
            UiScale = DeskUiScale.Clamp(scale);

        _self ??= DotNetObjectReference.Create(this);
        await module.InvokeVoidAsync("watch", _self);

        await PaintAsync();
        await NotifyAsync();
    }

    /// <summary>Records a new choice, persists it, and repaints. A no-op if nothing moved.</summary>
    public Task SetModeAsync(DeskThemeMode mode)
        => mode == Mode ? Task.CompletedTask : ChangeAsync(ModeKey, mode.ToString(), () => Mode = mode);

    public Task SetAccentAsync(DeskAccent accent)
        => accent == Accent ? Task.CompletedTask : ChangeAsync(AccentKey, accent.ToString(), () => Accent = accent);

    public Task SetTextSizeAsync(DeskTextSize size)
        => size == TextSize ? Task.CompletedTask : ChangeAsync(TextSizeKey, size.ToString(), () => TextSize = size);

    /// <summary>Snapped onto the nearest step first, so the stored value is always one the gate can show.</summary>
    public Task SetUiScaleAsync(int percent)
    {
        var snapped = DeskUiScale.Clamp(percent);

        return snapped == UiScale
            ? Task.CompletedTask
            : ChangeAsync(UiScaleKey, snapped.ToString(), () => UiScale = snapped);
    }

    /// <summary>Everything back to the desk the product ships as, in one press.</summary>
    public async Task ResetAppearanceAsync()
    {
        await SetModeAsync(DeskThemeMode.System);
        await SetAccentAsync(DeskAccent.Blue);
        await SetTextSizeAsync(DeskTextSize.Default);
        await SetUiScaleAsync(DeskUiScale.Default);
    }

    private async Task ChangeAsync(string key, string stored, Action apply)
    {
        apply();

        var module = await ModuleAsync();
        await module.InvokeVoidAsync("store", key, stored);

        await PaintAsync();
        await NotifyAsync();
    }

    /// <summary>
    /// The machine changed its mind — sunset, or a settings toggle. Called from theme.js.
    /// </summary>
    /// <remarks>
    /// The preference is recorded whatever the mode, rather than only under
    /// <see cref="DeskThemeMode.System"/>: an operator sitting on <see cref="DeskThemeMode.Light"/>
    /// whose machine goes dark and who then switches back to System must land on dark, and
    /// they will not if this method took an early exit and left the flag stale.
    /// </remarks>
    [JSInvokable]
    public async Task OnSystemPreferenceChanged(bool prefersDark)
    {
        if (prefersDark == _systemPrefersDark)
            return;

        _systemPrefersDark = prefersDark;

        if (Mode != DeskThemeMode.System)
            return;

        await PaintAsync();
        await NotifyAsync();
    }

    /// <summary>
    /// One call carrying everything, so the browser never holds a half-applied appearance
    /// between two round trips — a new accent under the old theme for a frame is exactly
    /// the kind of flash a scoped service exists to prevent.
    /// </summary>
    private async Task PaintAsync()
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync(
            "apply",
            IsDark ? DarkTheme : LightTheme,
            DeskAccents.Key(Accent),
            DeskTextSizes.Scale(TextSize),
            DeskUiScale.Factor(UiScale));
    }

    private Task NotifyAsync() => Changed?.Invoke() ?? Task.CompletedTask;

    private async ValueTask<IJSObjectReference> ModuleAsync()
        => _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);

    /// <summary>
    /// Tears the matchMedia listener down with the circuit.
    /// </summary>
    /// <remarks>
    /// Both calls are wrapped: disposal routinely runs <i>after</i> the circuit has gone,
    /// and reaching for JS then throws <see cref="JSDisconnectedException"/> — the documented
    /// shape of "the browser is already gone", not an error worth surfacing. There is nothing
    /// left to clean up in that case anyway, because the page holding the listener is closed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("unwatch");
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            /* the browser left first */
        }

        _self?.Dispose();
    }
}
