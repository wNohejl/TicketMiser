using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MudBlazor;
using MudBlazor.Services;
using TicketMiser.Core.Analytics;
using TicketMiser.Data;
using TicketMiser.Desk;
using TicketMiser.Ingestion;
using TicketMiser.Core.Entities;
using TicketMiser.Observability;
using TicketMiser.Reliability;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Accounts;
using TicketMiser.Web.Components;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Reports;
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

// Affiliate deep links for price cells (SourceLink.Tagged). Empty by default, so every link is
// the canonical event page until a programme's ids are configured.
builder.Services.Configure<AffiliateOptions>(builder.Configuration.GetSection(AffiliateOptions.SectionName));

// The public record page is rendered once and served from memory for five minutes: a shared
// link that lands a few hundred readers in a minute costs one query, and a tick written in
// between reaches them on the next render. Only GET /e/{slug} opts in; nothing else is cached.
const string EventRecordCache = "event-record";
// The calendar changes once a day, when discovery runs; a subscription that polls every few
// hours therefore reads a cached document almost every time.
const string OnSaleCalendarCache = "onsale-calendar";
// A report changes only when a commit publishes one, and a deploy starts a new process with an
// empty cache, so an hour costs nothing in freshness.
const string ReportsCache = "reports";
builder.Services.AddOutputCache(options =>
{
    // Varies by ?subscribed= only, so the subscribe form's "check your inbox" notice is its own
    // cached copy and any other query string reads the one shared document.
    options.AddPolicy(EventRecordCache, policy => policy.Expire(TimeSpan.FromMinutes(5)).SetVaryByQuery("subscribed"));
    options.AddPolicy(OnSaleCalendarCache, policy => policy.Expire(TimeSpan.FromMinutes(15)));
    // Varies by ?subscribed= for the report form's notice, as the record page does for its own.
    options.AddPolicy(ReportsCache, policy => policy.Expire(TimeSpan.FromHours(1)).SetVaryByQuery("subscribed"));
});
builder.Services.AddSingleton<IOnSaleCalendarService, OnSaleCalendarService>();

// The monthly reports, from docs/reports as copied beside the assembly (or Reports:Path).
builder.Services.AddSingleton<IReportLibrary>(FileReportLibrary.From(builder.Configuration));
// Mails a published month to the report list; only the send-report command below runs it.
builder.Services.AddScoped<ReportMailingService>();

// The subscribe form is the one anonymous write on the site. Ten posts per client address per
// ten minutes is more than a person needs and less than a flood of confirmation emails.
const string SubscribeLimit = "subscribe";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(SubscribeLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));

    // The sign-in form is the other anonymous write, and sends an email the same way.
    options.AddPolicy(AccountEndpoints.SignInLimit, AccountEndpoints.SignInPartition);
});

// Magic-link accounts: a cookie naming the account, no Identity, no password. Whoever is
// not signed in is the operator (OwnerScope), so the desk reads every row as it always has.
builder.Services.AddAccountSignIn();

// What the operations windows read. Each is an interface over the reliability layer and the
// database so a panel holds no EF query of its own and a render test can hand it a snapshot.
// Singletons: none holds state, each opens a scope or a context per call.
builder.Services.AddSingleton<IOpsQueries, OpsQueries>();
builder.Services.AddSingleton<IIncidentQueries, IncidentQueries>();
builder.Services.AddSingleton<IRunQueries, RunQueries>();
builder.Services.AddSingleton<IHistoryQueries, HistoryQueries>();

// The board's read: every enabled watch with each market's best number and its rail, composed
// per market and never across them. A context per call, like the rest. Scoped, as are the
// three below, because each reads on behalf of whoever is reading (IOwnerContext): an account
// sees its own watches and purchases, the operator sees all of them.
builder.Services.AddScoped<IWatchlistQueries, WatchlistQueries>();

// One event's prices over time — Price history, the Event window's history tab, Trend and All
// sources — and the Performers, Performer and Venue windows. Per market, never across; the
// composition is in Core. A context per call.
builder.Services.AddScoped<IPriceHistoryQueries, PriceHistoryQueries>();
builder.Services.AddScoped<IDestinationQueries, DestinationQueries>();

// The purchase ledger: Purchases, Savings, the Log purchase follow-up and the receipt export.
// Graded against day-of prices of the same all-in kind only. A context per call.
builder.Services.AddScoped<IPurchaseQueries, PurchaseQueries>();

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

// `send-report <yyyy-mm>`: the operator mails a published month's report to the report list,
// then the process exits. The host never starts, so no scheduler, evaluator or server runs.
if (SendReportCommand.Parse(args) is { } sendReport)
{
    Environment.ExitCode = await SendReportCommand.RunAsync(app.Services, sendReport, Console.Out);
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Authentication first: the output cache must know a request is signed in, so that it
// neither stores nor serves a copy of a page drawn for one account.
app.UseAuthentication();
app.UseAntiforgery();
app.UseRateLimiter();
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
// A signed-in fan's copy carries "Watch this event" and is not cached: the cache policy keeps
// the default rule that an authenticated request is neither stored nor served from the cache.
app.MapGet("/e/{id:int}", async (int id, HttpContext http, IOnSaleRecordService records, CancellationToken ct) =>
        await records.SlugForAsync(id, ct) is { } slug
            ? Results.Redirect($"/e/{slug}", permanent: true)
            : new RazorComponentResult<EventRecord>(new { EventId = id, OwnerScope.From(http.User).AccountId }))
    .AllowAnonymous();

app.MapGet("/e/{slug}", (string slug, string? subscribed, HttpContext http) =>
        new RazorComponentResult<EventRecord>(new { Slug = slug, Subscribed = subscribed, OwnerScope.From(http.User).AccountId }))
    .AllowAnonymous()
    .CacheOutput(EventRecordCache);

// "Tell me if face value comes back": double opt-in, per event (legal-guidelines rule 8).
// The post stores an unconfirmed address and sends one confirmation, at most hourly; nothing
// else reaches the address until its link is followed. Every accepted post gets the same 303,
// whether the address was new, pending or confirmed, so the form reveals nobody's subscription.
// No antiforgery token: the page is a cached static document with no cookie to pair one with,
// and double opt-in makes a forged post harmless — it can only ask the owner of the address.
// The rate limit is what stops that ask from being repeated at volume.
app.MapPost("/e/{slug}/subscribe", async (string slug, [FromForm] string? email, HttpContext http,
        SubscriptionService subscriptions, CancellationToken ct) =>
    {
        var result = await subscriptions.SubscribeAsync(slug, email, ct);
        if (result.Outcome == SubscribeOutcome.EventNotFound)
            return Results.NotFound();

        var flag = result.Outcome == SubscribeOutcome.InvalidAddress ? "invalid" : "pending";
        http.Response.Headers.Location = $"/e/{Uri.EscapeDataString(result.Slug!)}?subscribed={flag}";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    })
    .AllowAnonymous()
    .DisableAntiforgery()
    .RequireRateLimiting(SubscribeLimit);

// The links in the emails. Tokens are 32 random bytes; the pages are static, uncached and
// unindexed. Unsubscribe is RFC 8058 one-click: GET shows a button (a link scanner must not
// unsubscribe anyone), POST unsubscribes — from that button or a mail client's own — without
// antiforgery, because the token is the credential. Unknown tokens get the same page.
app.MapGet("/s/confirm/{token}", async (string token, HttpContext http, SubscriptionService subscriptions, CancellationToken ct) =>
    {
        var confirmed = await subscriptions.ConfirmAsync(token, ct);
        http.Response.Headers.CacheControl = "no-store";

        return new RazorComponentResult<SubscriptionPage>(new
        {
            Kind = confirmed is null ? SubscriptionPageKind.ConfirmUnknown : SubscriptionPageKind.Confirmed,
            EventName = confirmed?.EventName,
            Slug = confirmed?.Slug
        })
        { StatusCode = confirmed is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK };
    })
    .AllowAnonymous();

app.MapGet("/s/unsubscribe/{token}", (string token, HttpContext http) =>
    {
        http.Response.Headers.CacheControl = "no-store";
        return new RazorComponentResult<SubscriptionPage>(new { Kind = SubscriptionPageKind.UnsubscribePrompt, Token = token });
    })
    .AllowAnonymous();

app.MapPost("/s/unsubscribe/{token}", async (string token, HttpContext http, SubscriptionService subscriptions, CancellationToken ct) =>
    {
        await subscriptions.UnsubscribeAsync(token, ct);
        http.Response.Headers.CacheControl = "no-store";
        return new RazorComponentResult<SubscriptionPage>(new { Kind = SubscriptionPageKind.Unsubscribed });
    })
    .AllowAnonymous()
    .DisableAntiforgery();

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

// The monthly Nashville reports: the list, and one month's report. Static, anonymous and cached
// like the calendar. A month is served only when its file says `published: true`; an
// unpublished or missing month is a 404. The report list's form and email links are mapped with
// them, rate-limited like /e/{slug}/subscribe; mailing a month is the send-report command above.
app.MapReports(ReportsCache, SubscribeLimit);

// The ledger as a dated receipt (legal guidelines, rule 9): one event's on-sale record and the
// purchases logged against it, as a Markdown file to keep or attach to a complaint. A file, not
// a page ("exports are files"), so it is served as an attachment and never cached.
// Purchases are read through the ownership rule: a request carrying an account's cookie gets
// that account's purchases only; a request with no account is the operator's, unchanged, and
// carries every purchase logged against the event. That last case is as open as the desk it
// serves — exposing the desk publicly will need an operator sign-in, which is not built yet.
app.MapGet("/receipts/{eventId:int}.md", async (int eventId, HttpContext http, IOnSaleRecordService records,
        IPurchaseQueries purchases, TimeProvider clock, CancellationToken ct) =>
    {
        if (await records.GetAsync(eventId, ct) is not { } record)
            return Results.NotFound();

        var now = clock.GetUtcNow();
        var lines = await purchases.ForEventAsync(eventId, ct);
        var markdown = PurchaseReceipt.Write(record, lines, now, $"{http.Request.Scheme}://{http.Request.Host}");

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{PurchaseReceipt.FileName(record.Event, now)}\"";
        return Results.Text(markdown, PurchaseReceipt.ContentType);
    });

app.MapAccountEndpoints();

// Global Interactive Server render mode: MudBlazor does not support static server
// rendering, so interactivity is declared once at the root rather than per component.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
