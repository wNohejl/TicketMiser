using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketMiser.Desk.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// A panel refreshes itself when the data it names changes, once per burst, and not for data
/// it does not draw from. Without it, every window shows the data as of the last click. The
/// settle is waited out on a clock the test advances, so nothing here races a real timer.
/// </summary>
public class PanelRefreshTests : DeskTestContext
{
    /// <summary>A panel that watches one topic and counts its reloads.</summary>
    private sealed class CountingPanel : DeskPanel
    {
        public int Refreshes;

        protected override IReadOnlySet<string> Watches { get; } = new HashSet<string> { "prices" };

        protected override Task RefreshAsync()
        {
            Interlocked.Increment(ref Refreshes);
            return Task.CompletedTask;
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, $"refreshed {Refreshes}");
    }

    private readonly FakeTimeProvider _clock = new();

    /// <summary>The settle is the clock's to give; this is only room for a cold render on a full, parallel run.</summary>
    private static readonly TimeSpan Rendered = TimeSpan.FromSeconds(5);

    public PanelRefreshTests() => Services.AddSingleton<TimeProvider>(_clock);

    private DeskSignals Signals => Services.GetRequiredService<DeskSignals>();

    [Fact]
    public void A_burst_of_changes_is_one_refresh_after_the_settle()
    {
        var panel = Render<CountingPanel>();

        Signals.Publish(new HashSet<string> { "prices" });
        Signals.Publish(new HashSet<string> { "prices", "runs" });
        Signals.Publish(new HashSet<string> { "prices" });

        // Nothing until the burst has had its second to settle.
        Assert.Equal(0, panel.Instance.Refreshes);
        _clock.Advance(DeskPanel.Settle);

        panel.WaitForAssertion(() => Assert.Equal("refreshed 1", panel.Markup), Rendered);

        // And nothing further arrives once the burst has settled.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, panel.Instance.Refreshes);
    }

    [Fact]
    public void A_change_after_the_refresh_is_a_second_refresh()
    {
        var panel = Render<CountingPanel>();

        Signals.Publish(new HashSet<string> { "prices" });
        _clock.Advance(DeskPanel.Settle);
        panel.WaitForAssertion(() => Assert.Equal("refreshed 1", panel.Markup), Rendered);

        Signals.Publish(new HashSet<string> { "prices" });
        _clock.Advance(DeskPanel.Settle);
        panel.WaitForAssertion(() => Assert.Equal("refreshed 2", panel.Markup), Rendered);
    }

    [Fact]
    public void A_change_to_data_the_panel_does_not_draw_from_is_ignored()
    {
        var panel = Render<CountingPanel>();

        Signals.Publish(new HashSet<string> { "purchases", "alerts" });
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, panel.Instance.Refreshes);
    }

    [Fact]
    public async Task A_closed_panel_stops_listening()
    {
        var panel = Render<CountingPanel>();
        var instance = panel.Instance;

        await DisposeComponentsAsync();
        Signals.Publish(new HashSet<string> { "prices" });
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, instance.Refreshes);
    }
}
