using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// Ctrl+K: one field that reaches anything. Before anything is typed it is a launcher over the
/// windows; typing asks the application's sources after a pause; arrows and Enter pick; Escape
/// closes. The pause is on a clock the test advances.
/// </summary>
public class CommandPaletteTests : DeskTestContext
{
    private sealed class FakeSearch : IDeskSearch
    {
        public int Opened;
        public readonly List<string> Searches = [];

        public Task<IReadOnlyList<DeskSearchResult>> SearchAsync(string text, CancellationToken ct)
        {
            lock (Searches)
                Searches.Add(text);

            // "perf" finds a performer who shares the start of a window's name.
            IReadOnlyList<DeskSearchResult> results =
                text.Contains("ryman", StringComparison.OrdinalIgnoreCase)
                    ? [new DeskSearchResult("Venues", "Ryman Auditorium", "Nashville, TN", Icons.Material.Filled.Place, () => Opened++)]
                    : text.Contains("perf", StringComparison.OrdinalIgnoreCase)
                        ? [new DeskSearchResult("Performers", "Perfidia Lane", null, Icons.Material.Filled.Person, () => Opened++)]
                        : [];
            return Task.FromResult(results);
        }
    }

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// No timer is raced here — the pause is the clock's to give — but a cold render on a full,
    /// parallel run can still take more than bUnit's default second to be scheduled at all.
    /// </summary>
    private static readonly TimeSpan Rendered = TimeSpan.FromSeconds(5);

    private readonly FakeSearch _search = new();
    private readonly FakeTimeProvider _clock = new();

    public CommandPaletteTests()
    {
        Services.AddSingleton<IDeskSearch>(_search);
        Services.AddSingleton<TimeProvider>(_clock);
    }

    private WindowManager Manager => Services.GetRequiredService<WindowManager>();

    private IRenderedComponent<CommandPalette> Open()
    {
        var palette = Render<CommandPalette>();
        Manager.RequestPalette();
        palette.WaitForState(() => palette.FindAll("[role=dialog]").Count == 1, Rendered);
        return palette;
    }

    private static List<string> Options(IRenderedComponent<CommandPalette> palette)
        => palette.FindAll("[role=option]").Select(o => o.TextContent.Trim()).ToList();

    [Fact]
    public void It_is_closed_until_asked_for()
        => Assert.Empty(Render<CommandPalette>().FindAll("[role=dialog]"));

    [Fact]
    public void Opened_empty_it_lists_the_windows_that_open_on_nothing()
    {
        var palette = Open();
        var titles = Options(palette);

        Assert.Contains(titles, t => t.StartsWith(WindowCatalog.Find(WindowCatalog.Watchlist)!.Title));

        // Every window that opens on nothing, and every workspace — and no window that needs an
        // event, which opened cold would have nothing to show.
        var expected = Manager.Catalog.All.Count(d => !d.RequiresSubject) + Manager.Catalog.Workspaces.Count;
        Assert.Equal(expected, titles.Count);
    }

    [Fact]
    public void Typing_asks_the_applications_sources_after_the_pause_and_their_results_lead()
    {
        var palette = Open();

        palette.Find("input").Input("ryman");

        // A keystroke is not a query: nothing is asked until the typing pauses.
        Assert.Empty(_search.Searches);
        _clock.Advance(Pause);

        palette.WaitForAssertion(() => Assert.StartsWith("Ryman Auditorium", Options(palette)[0]), Rendered);
        Assert.Equal(["ryman"], _search.Searches);
    }

    [Fact]
    public void A_window_named_by_what_was_typed_leads_the_applications_results()
    {
        var palette = Open();

        palette.Find("input").Input("perf");
        _clock.Advance(Pause);
        palette.WaitForAssertion(() => Assert.Contains("Perfidia Lane", palette.Markup), Rendered);

        var options = Options(palette);
        Assert.StartsWith(WindowCatalog.Find(WindowCatalog.Performers)!.Title, options[0]);
        Assert.StartsWith("Perfidia Lane", options[1]);
    }

    [Fact]
    public void Enter_opens_the_highlighted_result_and_closes_the_palette()
    {
        var palette = Open();
        palette.Find("input").Input("ryman");
        _clock.Advance(Pause);
        palette.WaitForAssertion(() => Assert.Contains("Ryman Auditorium", palette.Markup), Rendered);

        palette.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(1, _search.Opened);
        Assert.Empty(palette.FindAll("[role=dialog]"));
    }

    [Fact]
    public void Choosing_a_window_opens_it_on_the_desk()
    {
        var palette = Open();
        var watchlist = WindowCatalog.Find(WindowCatalog.Watchlist)!.Title;

        palette.FindAll("[role=option]").First(o => o.TextContent.Trim().StartsWith(watchlist)).Click();

        Assert.Contains(Manager.Windows, w => w.Definition.Key == WindowCatalog.Watchlist);
        Assert.Empty(palette.FindAll("[role=dialog]"));
    }

    [Fact]
    public void Escape_closes_without_opening_anything()
    {
        var palette = Open();

        palette.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(palette.FindAll("[role=dialog]"));
        Assert.Equal(0, _search.Opened);
    }

    [Fact]
    public void Arrows_move_the_highlight_and_wrap()
    {
        var palette = Open();
        var count = palette.FindAll("[role=option]").Count;

        palette.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Equal("true", palette.Find($"#palette-{count - 1}").GetAttribute("aria-selected"));
    }
}
