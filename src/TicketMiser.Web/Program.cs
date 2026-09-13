using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using MudBlazor;
using MudBlazor.Services;
using TicketMiser.Core.Analytics;
using TicketMiser.Data;
using TicketMiser.Desk;
using TicketMiser.Ingestion;
using TicketMiser.Observability;
using TicketMiser.Reliability;
using TicketMiser.Web.Components;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;
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

// The queries the windows read. Each takes the context factory AddTicketMiserData registers,
// so a circuit never holds a context open between renders.
builder.Services.AddScoped<IOnSaleRecordService, OnSaleRecordService>();

// The public record page is rendered once and served from memory for five minutes: a shared
// link that lands a few hundred readers in a minute costs one query, and a tick written in
// between reaches them on the next render. Only GET /e/{slug} opts in; nothing else is cached.
const string EventRecordCache = "event-record";
// The calendar changes once a day, when discovery runs; a subscription that polls every few
// hours therefore reads a cached document almost every time.
const string OnSaleCalendarCache = "onsale-calendar";
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy(EventRecordCache, policy => policy.Expire(TimeSpan.FromMinutes(5)));
    options.AddPolicy(OnSaleCalendarCache, policy => policy.Expire(TimeSpan.FromMinutes(15)));
});
builder.Services.AddSingleton<IOnSaleCalendarService, OnSaleCalendarService>();

// What the operations windows read. Each is an interface over the reliability layer and the
// database so a panel holds no EF query of its own and a render test can hand it a snapshot.
// Singletons: none holds state, each opens a scope or a context per call.
builder.Services.AddSingleton<IOpsQueries, OpsQueries>();
builder.Services.AddSingleton<IIncidentQueries, IncidentQueries>();
builder.Services.AddSingleton<IRunQueries, RunQueries>();
builder.Services.AddSingleton<IHistoryQueries, HistoryQueries>();

// The board's read: every enabled watch with each market's best number and its rail, composed
// per market and never across them. A context per call, like the rest.
builder.Services.AddSingleton<IWatchlistQueries, WatchlistQueries>();

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
app.UseOutputCache();

// Liveness answers for the process only; readiness is what compose gates on.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ObservabilityServiceCollectionExtensions.ReadyTag)
}).AllowAnonymous();

app.MapStaticAssets();

// The public on-sale record, outside the desk. Static and anonymous: EventRecord is rendered
// as a whole document with no circuit behind it (the note at the top of that component says
// why it carries no @page), so a fan with a link needs no account and loads no script.
// GET /e/{id} is the address the desk can build before a row has a slug; it redirects to
// the slug once one exists and renders the record by id until then.
app.MapGet("/e/{id:int}", async (int id, IOnSaleRecordService records, CancellationToken ct) =>
        await records.SlugForAsync(id, ct) is { } slug
            ? Results.Redirect($"/e/{slug}", permanent: true)
            : new RazorComponentResult<EventRecord>(new { EventId = id }))
    .AllowAnonymous();

app.MapGet("/e/{slug}", (string slug) => new RazorComponentResult<EventRecord>(new { Slug = slug }))
    .AllowAnonymous()
    .CacheOutput(EventRecordCache);

// The on-sale calendar: the page, and the same entries as an iCalendar feed a client
// subscribes to once. Both anonymous and cached; the feed costs no quota to build because
// the discovery run already wrote every time it lists.
app.MapGet("/onsales", () => new RazorComponentResult<OnSales>())
    .AllowAnonymous()
    .CacheOutput(OnSaleCalendarCache);

app.MapGet("/onsales.ics", async (HttpContext http, IOnSaleCalendarService calendar, TimeProvider clock, CancellationToken ct) =>
    {
        var now = clock.GetUtcNow();
        var entries = await calendar.LoadAsync(now, ct);
        var ics = OnSaleIcs.Write(entries, "Nashville on-sales", $"{http.Request.Scheme}://{http.Request.Host}", now);
        http.Response.Headers.ContentDisposition = "inline; filename=\"nashville-onsales.ics\"";
        return Results.Text(ics, OnSaleIcs.ContentType);
    })
    .AllowAnonymous()
    .CacheOutput(OnSaleCalendarCache);

// Global Interactive Server render mode: MudBlazor does not support static server
// rendering, so interactivity is declared once at the root rather than per component.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
