using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MudBlazor;
using MudBlazor.Services;
using TicketMiser.Data;
using TicketMiser.Desk;
using TicketMiser.Ingestion;
using TicketMiser.Observability;
using TicketMiser.Reliability;
using TicketMiser.Web.Components;
using TicketMiser.Web.Windowing;

var builder = WebApplication.CreateBuilder(args);

// Local overrides, last so they win. Source keys live here: the file is gitignored, which the
// environment-named ones are not. Optional, so a clone with no keys still starts.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

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

// The desk comes from TicketMiser.Desk and knows nothing about tickets. It is told what this
// application is called, and reads its windows from the catalogue registered beneath it.
builder.Services.AddDesk(new DeskBrand("TICKET", "MISER", "ticket prices, compiled and remembered"));
builder.Services.AddSingleton<IWindowCatalog, AppWindowCatalog>();

// Persist Data Protection keys outside the container when a path is configured, so a
// replaced container does not invalidate every live circuit.
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
        .SetApplicationName("TicketMiser");
}

builder.Services.AddTicketMiserData(builder.Configuration);
builder.Services.AddTicketMiserIngestion(builder.Configuration);
builder.Services.AddTicketMiserReliability(builder.Configuration);
builder.Services.AddTicketMiserObservability(builder.Configuration);
builder.Services.AddTicketMiserHealthChecks();

// Single-process deployment: the web app also hosts the schedule and the evaluator. Both are
// opt-in, so splitting the worker out later is a startup change rather than a refactor.
if (builder.Configuration.GetValue("Ingestion:HostScheduler", true))
    builder.Services.AddTicketMiserIngestionScheduler();

if (builder.Configuration.GetValue("Reliability:HostEvaluator", true))
    builder.Services.AddTicketMiserReliabilityEvaluator();

var app = builder.Build();

// Migrate, ensure partitions exist ahead of the clock, and seed reference rows.
await using (var scope = app.Services.CreateAsyncScope())
{
    var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initialiser.InitialiseAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

// Liveness answers for the process only; readiness is what compose gates on.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ObservabilityServiceCollectionExtensions.ReadyTag)
}).AllowAnonymous();

app.MapStaticAssets();

// Global Interactive Server render mode: MudBlazor does not support static server
// rendering, so interactivity is declared once at the root rather than per component.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
