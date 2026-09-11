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
/// This is the domain-free half of what LineOps called <c>PanelBase</c>; the date and price
/// formatters that sat beside it there belong to that product, and a host adds its own.
/// </summary>
public abstract class DeskPanel : ComponentBase
{
    [CascadingParameter(Name = "WindowId")]
    protected string? WindowId { get; set; }

    [Inject] protected WindowManager Manager { get; set; } = default!;

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
