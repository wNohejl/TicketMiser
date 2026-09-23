using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The bench every Desk render test sits on.
///
/// Desk primitives wrap MudBlazor, and MudBlazor components resolve services out of the
/// container and reach for JS the moment they render. Registering both here means a test
/// states only what it is checking — a role, an aria attribute, a class, a callback — and
/// never the plumbing that got the component onto the page.
///
/// JSInterop is loose on purpose: the desk's own modules (desk-glide and friends) are
/// browser behaviour, not markup semantics, and a test that had to stub each import would
/// break every time a component picked up an animation.
/// </summary>
public abstract class DeskTestContext : BunitContext, IAsyncLifetime
{
    // MudBlazor's PointerEventsNoneService and the desk's ThemeService are async-only
    // disposables. bunit 2's synchronous Dispose hands them to a container that refuses them,
    // and xUnit 2 only ever calls Dispose on a test class; routing teardown through
    // IAsyncLifetime disposes the context asynchronously first, and the later Dispose is a no-op.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    protected DeskTestContext()
    {
        Services.AddMudServices();

        // DeskChart asks which desk is showing, because a chart's series colours reach
        // MudBlazor's SVG renderer in C# and cannot be re-themed by a token block. Left
        // uninitialised on purpose: a service that has never been told otherwise reports
        // the dark desk, which is what every render assertion here was written against.
        Services.AddScoped<TicketMiser.Desk.Theming.ThemeService>();

        // The desk asks its host two things — what it is called and what it can open — and
        // a render test should not have to say either. The application's own catalogue is
        // the honest answer for the second: these tests read its keys by name.
        Services.AddSingleton(new TicketMiser.Desk.DeskBrand("TICKET", "MISER", "test desk"));
        Services.AddSingleton<TicketMiser.Desk.IWindowCatalog, TicketMiser.Web.Windowing.AppWindowCatalog>();

        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
