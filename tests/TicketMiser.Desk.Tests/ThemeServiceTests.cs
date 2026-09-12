using Bunit;
using MudBlazor.Utilities;
using TicketMiser.Desk.Theming;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The theme decision, exercised without a browser.
///
/// <para>
/// Everything worth testing here is the resolution of a three-position setting against a
/// machine preference that can move underneath it, and none of that is browser behaviour —
/// theme.js holds no policy on purpose, precisely so the policy can be tested in one place
/// and one language. The module is stubbed strictly (not with the loose mode the render
/// tests use) because <i>which</i> calls the service makes is part of what is being
/// checked: a mode change that forgets to persist would pass a loose stub silently.
/// </para>
/// </summary>
public class ThemeServiceTests : TestContext
{
    private const string DarkAttribute = "apple-dark";
    private const string LightAttribute = "light";

    /// <summary>
    /// Stands the module up with a stored choice and a machine preference.
    /// </summary>
    private BunitJSModuleInterop _module = default!;

    private ThemeService Arrange(string? stored, bool prefersDark)
        => Arrange(prefersDark, mode: stored);

    /// <summary>
    /// Each setting is its own localStorage key, so the stub answers <c>read</c> by key:
    /// a value the product has stopped writing is simply never read rather than breaking
    /// the parse of the ones it still does.
    /// </summary>
    private ThemeService Arrange(
        bool prefersDark,
        string? mode = null,
        string? accent = null,
        string? text = null,
        string? scale = null)
    {
        _module = JSInterop.SetupModule("./_content/TicketMiser.Desk/js/theme.js");

        _module.Setup<bool>("prefersDark").SetResult(prefersDark);
        _module.Setup<string?>("read", i => (string)i.Arguments[0]! == ThemeService.ModeKey).SetResult(mode);
        _module.Setup<string?>("read", i => (string)i.Arguments[0]! == ThemeService.AccentKey).SetResult(accent);
        _module.Setup<string?>("read", i => (string)i.Arguments[0]! == ThemeService.TextSizeKey).SetResult(text);
        _module.Setup<string?>("read", i => (string)i.Arguments[0]! == ThemeService.UiScaleKey).SetResult(scale);
        _module.SetupVoid("store", _ => true).SetVoidResult();
        _module.SetupVoid("watch", _ => true).SetVoidResult();
        _module.SetupVoid("unwatch").SetVoidResult();
        _module.SetupVoid("apply", _ => true).SetVoidResult();

        return new ThemeService(JSInterop.JSRuntime);
    }

    // Read out of the module's own invocation log rather than off the service, so the
    // assertions are about what the browser was actually told rather than about what was
    // intended. The log lives on the module handler, not the root one: an imported module's
    // calls never appear in JSInterop.Invocations, and a query against the root would return
    // an empty list that Last() turns into a failure and Contains() turns into a pass.
    private string LastPaint() => Calls("apply").Last();

    /// <summary>The whole last paint: theme, accent key, type scale, ui scale.</summary>
    private object?[] LastPaintArguments() => _module.Invocations["apply"].Last().Arguments.ToArray();

    /// <summary>Every value stored, under any key.</summary>
    private IEnumerable<string> Stored() => _module.Invocations["store"]
        .Select(i => (string)i.Arguments[1]!);

    private IEnumerable<(string Key, string Value)> StoredPairs() => _module.Invocations["store"]
        .Select(i => ((string)i.Arguments[0]!, (string)i.Arguments[1]!));

    private IEnumerable<string> Calls(string identifier) => _module.Invocations[identifier]
        .Select(i => (string)i.Arguments[0]!);

    private int CallCount(string identifier) => _module.Invocations[identifier].Count;

    [Fact]
    public async Task With_nothing_stored_the_desk_follows_the_machine()
    {
        var service = Arrange(stored: null, prefersDark: false);

        await service.InitializeAsync();

        Assert.Equal(DeskThemeMode.System, service.Mode);
        Assert.False(service.IsDark);
        Assert.Equal(LightAttribute, LastPaint());
    }

    /// <summary>
    /// A stored choice beats the machine outright — that is what choosing means, and a
    /// setting that quietly deferred to prefers-color-scheme would be a setting that did
    /// nothing for exactly the operators who bothered to change it.
    /// </summary>
    [Fact]
    public async Task A_stored_choice_overrides_the_machine()
    {
        var service = Arrange(stored: "Light", prefersDark: true);

        await service.InitializeAsync();

        Assert.Equal(DeskThemeMode.Light, service.Mode);
        Assert.False(service.IsDark);
        Assert.Equal(LightAttribute, LastPaint());
    }

    /// <summary>
    /// localStorage holds whatever the last version of the product wrote, plus whatever a
    /// curious operator typed into the console. An unparseable value is not an error state
    /// — it is simply not a choice, so the desk falls back to following the machine.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Sepia")]
    [InlineData("true")]
    public async Task An_unreadable_stored_value_falls_back_to_System(string stored)
    {
        var service = Arrange(stored, prefersDark: true);

        await service.InitializeAsync();

        Assert.Equal(DeskThemeMode.System, service.Mode);
        Assert.True(service.IsDark);
    }

    [Fact]
    public async Task Choosing_a_mode_persists_it_and_repaints()
    {
        var service = Arrange(stored: null, prefersDark: true);
        await service.InitializeAsync();

        await service.SetModeAsync(DeskThemeMode.Light);

        Assert.Contains("Light", Stored());
        Assert.Equal(LightAttribute, LastPaint());
    }

    /// <summary>
    /// The layout re-renders on this event and nothing else would tell MudThemeProvider to
    /// look again, so an unraised event is a desk whose own tokens have flipped and whose
    /// Mud components have not — the two halves of the product disagreeing on screen.
    /// </summary>
    [Fact]
    public async Task Changing_the_mode_announces_it()
    {
        var service = Arrange(stored: null, prefersDark: true);
        await service.InitializeAsync();

        var announced = 0;
        service.Changed += () => { announced++; return Task.CompletedTask; };

        await service.SetModeAsync(DeskThemeMode.Light);

        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Choosing_the_mode_already_showing_does_nothing()
    {
        var service = Arrange(stored: "Dark", prefersDark: true);
        await service.InitializeAsync();

        var announced = 0;
        service.Changed += () => { announced++; return Task.CompletedTask; };

        await service.SetModeAsync(DeskThemeMode.Dark);

        Assert.Equal(0, announced);
        Assert.Empty(Stored());
    }

    /// <summary>
    /// System has to keep meaning "system". A machine that turns dark at sunset moves the
    /// desk with it, which is the entire difference between three positions and two.
    /// </summary>
    [Fact]
    public async Task Under_System_the_desk_follows_the_machine_changing_its_mind()
    {
        var service = Arrange(stored: null, prefersDark: false);
        await service.InitializeAsync();

        Assert.False(service.IsDark);

        await service.OnSystemPreferenceChanged(prefersDark: true);

        Assert.True(service.IsDark);
        Assert.Equal(DarkAttribute, LastPaint());
    }

    [Fact]
    public async Task A_declared_mode_ignores_the_machine_changing_its_mind()
    {
        var service = Arrange(stored: "Light", prefersDark: false);
        await service.InitializeAsync();

        await service.OnSystemPreferenceChanged(prefersDark: true);

        Assert.False(service.IsDark);
        Assert.Equal(LightAttribute, LastPaint());
    }

    /// <summary>
    /// The subtle one, and the reason OnSystemPreferenceChanged records the preference
    /// before it checks the mode rather than after.
    ///
    /// An operator on Light whose machine goes dark, and who then returns to System, must
    /// land on dark. If the handler had taken an early exit while the mode was Light, the
    /// stored preference would still say "light" from initialisation and System would
    /// resolve to the wrong desk — a bug that only appears in the third step of a sequence
    /// nobody performs by accident.
    /// </summary>
    [Fact]
    public async Task A_preference_that_moved_while_overridden_is_still_remembered()
    {
        var service = Arrange(stored: "Light", prefersDark: false);
        await service.InitializeAsync();

        await service.OnSystemPreferenceChanged(prefersDark: true);
        await service.SetModeAsync(DeskThemeMode.System);

        Assert.True(service.IsDark);
        Assert.Equal(DarkAttribute, LastPaint());
    }

    /// <summary>
    /// The listener is registered exactly once, with the reference disposal will need. A
    /// second registration would double every system-preference repaint.
    /// </summary>
    [Fact]
    public async Task Initialisation_registers_one_system_listener()
    {
        var service = Arrange(stored: null, prefersDark: true);

        await service.InitializeAsync();

        Assert.Equal(1, CallCount("watch"));
    }

    [Fact]
    public async Task Disposal_takes_the_listener_down()
    {
        var service = Arrange(stored: null, prefersDark: true);
        await service.InitializeAsync();

        await service.DisposeAsync();

        Assert.Equal(1, CallCount("unwatch"));
    }

    // ---- The rest of the appearance ------------------------------------------

    /// <summary>
    /// With nothing stored the desk is the one the product ships as: blue, unscaled. That
    /// is what makes the one unavoidable frame before localStorage is read invisible to
    /// anyone who has never changed a setting.
    /// </summary>
    [Fact]
    public async Task With_nothing_stored_the_appearance_is_the_default()
    {
        var service = Arrange(prefersDark: true);

        await service.InitializeAsync();

        Assert.Equal(DeskAccent.Blue, service.Accent);
        Assert.Equal(DeskTextSize.Default, service.TextSize);
        Assert.Equal(DeskUiScale.Default, service.UiScale);
        Assert.Equal(new object?[] { DarkAttribute, "blue", 1.0, 1.0 }, LastPaintArguments());
    }

    [Fact]
    public async Task Stored_accent_text_size_and_scale_are_read_back()
    {
        var service = Arrange(prefersDark: true, accent: "Purple", text: "Large", scale: "125");

        await service.InitializeAsync();

        Assert.Equal(DeskAccent.Purple, service.Accent);
        Assert.Equal(DeskTextSize.Large, service.TextSize);
        Assert.Equal(125, service.UiScale);
        Assert.Equal(new object?[] { DarkAttribute, "purple", 1.12, 1.25 }, LastPaintArguments());
    }

    /// <summary>
    /// A scale is a number, and a number can be anything a previous version or a curious
    /// operator wrote. It is snapped to the nearest step the gate offers, so a stored 3
    /// cannot draw a desk at 3% and a stored 400 cannot draw one nobody can close.
    /// </summary>
    [Theory]
    [InlineData("3", 75)]
    [InlineData("118", 125)]
    [InlineData("104", 100)]
    [InlineData("400", 125)]
    public async Task A_stored_scale_off_the_steps_snaps_to_the_nearest(string stored, int expected)
    {
        var service = Arrange(prefersDark: true, scale: stored);

        await service.InitializeAsync();

        Assert.Equal(expected, service.UiScale);
    }

    [Theory]
    [InlineData("Sepia", "Huge", "big")]
    [InlineData("", "", "")]
    public async Task Unreadable_appearance_values_fall_back_to_the_defaults(string accent, string text, string scale)
    {
        var service = Arrange(prefersDark: true, accent: accent, text: text, scale: scale);

        await service.InitializeAsync();

        Assert.Equal(DeskAccent.Blue, service.Accent);
        Assert.Equal(DeskTextSize.Default, service.TextSize);
        Assert.Equal(DeskUiScale.Default, service.UiScale);
    }

    /// <summary>
    /// Each setting persists under its own key, and every change repaints the whole
    /// appearance in one call — the browser never holds a new accent under an old theme
    /// between two round trips.
    /// </summary>
    [Fact]
    public async Task Choosing_an_accent_persists_it_under_its_own_key_and_repaints()
    {
        var service = Arrange(prefersDark: true);
        await service.InitializeAsync();

        await service.SetAccentAsync(DeskAccent.Indigo);

        Assert.Contains((ThemeService.AccentKey, "Indigo"), StoredPairs());
        Assert.Equal(new object?[] { DarkAttribute, "indigo", 1.0, 1.0 }, LastPaintArguments());
        Assert.Equal(new MudColor(DeskAccents.Dark(DeskAccent.Indigo).Accent).Value, service.MudTheme.PaletteDark.Primary.Value);
    }

    [Fact]
    public async Task Choosing_a_text_size_persists_it_and_scales_the_ramp()
    {
        var service = Arrange(prefersDark: false, mode: "Light");
        await service.InitializeAsync();

        await service.SetTextSizeAsync(DeskTextSize.ExtraLarge);

        Assert.Contains((ThemeService.TextSizeKey, "ExtraLarge"), StoredPairs());
        Assert.Equal(new object?[] { LightAttribute, "blue", 1.25, 1.0 }, LastPaintArguments());
    }

    [Fact]
    public async Task Choosing_a_scale_snaps_persists_and_zooms()
    {
        var service = Arrange(prefersDark: true);
        await service.InitializeAsync();

        await service.SetUiScaleAsync(112);

        Assert.Equal(110, service.UiScale);
        Assert.Contains((ThemeService.UiScaleKey, "110"), StoredPairs());
        Assert.Equal(new object?[] { DarkAttribute, "blue", 1.0, 1.1 }, LastPaintArguments());
    }

    [Fact]
    public async Task Choosing_what_is_already_showing_does_nothing()
    {
        var service = Arrange(prefersDark: true, accent: "Teal", text: "Small", scale: "90");
        await service.InitializeAsync();

        var announced = 0;
        service.Changed += () => { announced++; return Task.CompletedTask; };

        await service.SetAccentAsync(DeskAccent.Teal);
        await service.SetTextSizeAsync(DeskTextSize.Small);
        await service.SetUiScaleAsync(90);

        Assert.Equal(0, announced);
        Assert.Empty(Stored());
    }

    [Fact]
    public async Task Resetting_the_appearance_returns_every_setting_to_its_default()
    {
        var service = Arrange(prefersDark: true, mode: "Light", accent: "Graphite", text: "Large", scale: "75");
        await service.InitializeAsync();

        await service.ResetAppearanceAsync();

        Assert.Equal(DeskThemeMode.System, service.Mode);
        Assert.Equal(DeskAccent.Blue, service.Accent);
        Assert.Equal(DeskTextSize.Default, service.TextSize);
        Assert.Equal(DeskUiScale.Default, service.UiScale);
        Assert.Equal(new object?[] { DarkAttribute, "blue", 1.0, 1.0 }, LastPaintArguments());
    }
}
