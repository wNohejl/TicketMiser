using MudBlazor;
using MudBlazor.Services;
using TicketMiser.Desk;
using TicketMiser.Web.Components;
using TicketMiser.Web.Windowing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(options =>
{
    // Toasts land bottom-right, clear of the strip. Newest on top so a burst reads in the
    // order it happened. Duplicates allowed: a notice derived from an outcome that repeats
    // should repeat, or the second press looks as if it did nothing.
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    options.SnackbarConfiguration.NewestOnTop = true;
    options.SnackbarConfiguration.PreventDuplicates = false;
    options.SnackbarConfiguration.MaxDisplayedSnackbars = 4;
    options.SnackbarConfiguration.VisibleStateDuration = 4000;
    options.SnackbarConfiguration.ShowCloseIcon = true;
    options.SnackbarConfiguration.ShowTransitionDuration = 120;
    options.SnackbarConfiguration.HideTransitionDuration = 200;
});

// The desk — window manager, toasts, confirms, theme — comes from TicketMiser.Desk and knows
// nothing about tickets. It is told what this application is called, and reads its windows
// from the catalogue registered beneath it.
builder.Services.AddDesk(new DeskBrand("TICKET", "MISER", "ticket prices, compiled and remembered"));
builder.Services.AddSingleton<IWindowCatalog, AppWindowCatalog>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();

// Global Interactive Server render mode: MudBlazor does not support static server
// rendering, so interactivity is declared once at the root rather than per component.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
