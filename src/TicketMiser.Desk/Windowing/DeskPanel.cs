using Microsoft.AspNetCore.Components;

namespace TicketMiser.Desk.Windowing;

/// <summary>
/// Shared plumbing for a windowed panel.
///
/// A panel is an ordinary component — it does not know how it is displayed. Its only
/// coupling to the window system is the cascaded id, which lets it push its own state
/// onto its title bar and strip key. That inversion is what makes "any view can be a
/// window" true: panels are hostable anywhere, and the chrome reads from them.
///
/// This is the domain-free half of what the desk's previous host called <c>PanelBase</c>; the date and price
/// formatters that sat beside it there belong to that product, and a host adds its own.
///
/// <para>
/// A panel that names the data it draws from (<see cref="Watches"/>) is refreshed when that data
/// changes, whichever process wrote it: <see cref="DeskSignals"/> carries the application's change
/// notices, and the panel reloads through <see cref="RefreshAsync"/>. That is what makes a
/// minimised window's pulse mean something — without it, every window shows the data as of the
/// last click.
/// </para>
/// </summary>
public abstract class DeskPanel : ComponentBase, IDisposable
{
    /// <summary>
    /// How long a panel waits after a change before reloading. A poll run or a backfill writes
    /// in bursts; one reload per burst is the point, and a second's lag is invisible.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private bool _subscribed;
    private bool _refreshQueued;
    private bool _disposed;

    [CascadingParameter(Name = "WindowId")]
    protected string? WindowId { get; set; }

    [Inject] protected WindowManager Manager { get; set; } = default!;

    [Inject] private DeskSignals Signals { get; set; } = default!;

    /// <summary>The settle is waited out on the host's clock, so a test gives it rather than racing a timer.</summary>
    [Inject] private TimeProvider SettleClock { get; set; } = default!;

    /// <summary>The data topics this panel draws from, as the application names them. Empty: never refreshed.</summary>
    protected virtual IReadOnlySet<string> Watches { get; } = new HashSet<string>();

    /// <summary>Reloads the panel's data after a change it watches. The re-render is done for it.</summary>
    protected virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>For a panel with its own subscriptions to release. Called once, on disposal.</summary>
    protected virtual void OnDispose()
    {
    }

    public override Task SetParametersAsync(ParameterView parameters)
    {
        // Here rather than in OnInitialized, which every panel overrides and would have to
        // remember to pass on.
        if (!_subscribed && Watches.Count > 0)
        {
            Signals.Changed += OnSignal;
            _subscribed = true;
        }

        return base.SetParametersAsync(parameters);
    }

    private void OnSignal(IReadOnlySet<string> topics)
    {
        if (_disposed || !topics.Overlaps(Watches))
            return;

        lock (_gate)
        {
            if (_refreshQueued)
                return;

            _refreshQueued = true;
        }

        _ = RefreshSoonAsync();
    }

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(Settle, SettleClock);

        lock (_gate)
            _refreshQueued = false;

        if (_disposed)
            return;

        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed)
                    return;

                await RefreshAsync();
                StateHasChanged();
            });
        }
        catch (Exception) when (!_disposed)
        {
            // A refresh nobody asked for must not take the circuit down; the pulse says it
            // failed, and the next change or a click tries again.
            Report(PulseState.Warn, "refresh failed");
        }
        catch (Exception)
        {
            // Disposed while the reload was in flight: nothing left to show it on.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_subscribed)
            Signals.Changed -= OnSignal;

        OnDispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Reports this panel's state to its chrome. No-op when hosted outside a window.</summary>
    protected void Report(PulseState state, string? status = null)
    {
        if (WindowId is not null)
            Manager.SetPulse(WindowId, state, status);
    }

    /// <summary>
    /// Runs a load with the pulse showing activity, then settles it on the outcome.
    /// Wrapping it here means every panel reports progress the same way rather than each
    /// remembering to.
    /// </summary>
    protected async Task WithActivityAsync(Func<Task> work, Func<PulseState> settle, Func<string?>? status = null)
    {
        Report(PulseState.Active);

        try
        {
            await work();
            Report(settle(), status?.Invoke());
        }
        catch
        {
            Report(PulseState.Critical, "load failed");
            throw;
        }
    }
}
