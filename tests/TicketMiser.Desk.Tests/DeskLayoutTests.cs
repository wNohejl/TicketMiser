using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The desk written down and put back. A reload used to lose the row, the ceiling and the
/// primary — they lived only in the circuit — and there was no way to keep an arrangement.
/// </summary>
public class DeskLayoutTests
{
    private static WindowManager NewManager() => new(new AppWindowCatalog());

    private static WindowDefinition Window(string key) => WindowCatalog.Find(key)!;

    [Fact]
    public void A_desk_survives_being_written_down_and_read_back()
    {
        var before = NewManager();
        before.UpdateSettings(s => s.MaxConcurrentWindows = 5);
        before.Open(Window(WindowCatalog.Watchlist));
        var evt = before.Open(Window(WindowCatalog.Event), new Dictionary<string, object> { [Destinations.EventId] = 3470 }, "Night Owls at the Ryman");
        var ops = before.Open(Window(WindowCatalog.Ops));
        before.ToggleMinimise(ops.Id);
        before.Focus(evt.Id);

        var json = before.Capture().Serialise();

        var after = NewManager();
        after.Restore(DeskLayout.Parse(json)!);

        Assert.Equal(
            [WindowCatalog.Watchlist, WindowCatalog.Event, WindowCatalog.Ops],
            after.Windows.OrderBy(w => w.Sequence).Select(w => w.Definition.Key));

        var restoredEvent = after.Windows.Single(w => w.Definition.Key == WindowCatalog.Event);

        // The id comes back as the int the component parameter is declared as, not as JSON.
        Assert.Equal(3470, Assert.IsType<int>(restoredEvent.Parameters[Destinations.EventId]));
        Assert.Equal("Night Owls at the Ryman", restoredEvent.Title);
        Assert.Equal(restoredEvent.Id, after.FocusedId);
        Assert.True(after.Windows.Single(w => w.Definition.Key == WindowCatalog.Ops).Minimised);
        Assert.Equal(5, after.Settings.MaxConcurrentWindows);
    }

    [Fact]
    public void A_window_the_catalogue_no_longer_has_is_dropped_not_fatal()
    {
        var layout = new DeskLayout
        {
            MaxConcurrentWindows = 4,
            Windows =
            [
                new DeskLayoutWindow("board", null, 1, false, null),   // a key this catalogue never had
                new DeskLayoutWindow(WindowCatalog.Purchases, null, 1, false, null)
            ]
        };

        var manager = NewManager();
        manager.Restore(DeskLayout.Parse(layout.Serialise())!);

        Assert.Equal(WindowCatalog.Purchases, Assert.Single(manager.Windows).Definition.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"version\":99,\"windows\":[]}")]
    public void An_unreadable_or_foreign_layout_is_ignored(string? json)
        => Assert.Null(DeskLayout.Parse(json));

    [Fact]
    public void A_saved_workspace_keeps_the_row_but_not_windows_opened_on_something()
    {
        var manager = NewManager();
        manager.UpdateSettings(s => s.MaxConcurrentWindows = 5);
        manager.Open(Window(WindowCatalog.Watchlist));
        manager.Open(Window(WindowCatalog.Venue), new Dictionary<string, object> { [Destinations.VenueId] = 1 });
        manager.Open(Window(WindowCatalog.Purchases));

        var saved = manager.SaveWorkspace("  Friday  ");

        Assert.NotNull(saved);
        Assert.Equal("Friday", saved.Name);
        Assert.Equal([WindowCatalog.Watchlist, WindowCatalog.Purchases], saved.WindowKeys);

        // Saving under the same name replaces it, and it travels with the layout.
        manager.SaveWorkspace("friday");
        var restored = NewManager();
        restored.Restore(DeskLayout.Parse(manager.Capture().Serialise())!);

        Assert.Equal("friday", Assert.Single(restored.UserWorkspaces).Name);
    }
}
