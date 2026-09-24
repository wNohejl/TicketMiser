using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TicketMiser.Reliability.Notifications;
using TicketMiser.Web.Components.Pages;

namespace TicketMiser.Web.Reports;

/// <summary>
/// GET /reports and GET /reports/{yyyy-mm}: the monthly Nashville reports, as static whole
/// documents (<see cref="ReportPage"/>), anonymous and output-cached like the calendar.
///
/// <para>
/// A month is served only when its file says <c>published: true</c>; an unpublished or
/// missing month is a 404, which the output cache does not keep. Publishing is a commit.
/// </para>
///
/// <para>
/// The report list: POST /reports/subscribe, and the links in its emails under /r/. Its own
/// list with its own double opt-in (legal-guidelines rule 8), never the event alerts'
/// addresses. Mailing a published month is the operator's <c>send-report</c> command
/// (<see cref="SendReportCommand"/>); nothing here sends a report.
/// </para>
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReports(this IEndpointRouteBuilder app, string cachePolicy, string subscribeRateLimit)
    {
        app.MapGet("/reports", List).AllowAnonymous().CacheOutput(cachePolicy);
        app.MapGet("/reports/{month}", Month).AllowAnonymous().CacheOutput(cachePolicy);

        // "Get the monthly Nashville report by email". Every accepted post gets the same 303,
        // whether the address was new, pending or confirmed, so the form reveals nobody's
        // subscription. No antiforgery token, for the reason the event subscribe form has none:
        // the page is a cached static document with no cookie to pair one with, and double
        // opt-in makes a forged post harmless — it can only ask the owner of the address. The
        // rate limit, the same as /e/{slug}/subscribe, stops that ask being repeated at volume.
        app.MapPost("/reports/subscribe", SubscribeAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(subscribeRateLimit);

        // The links in the emails. Confirm is the sign-in link's pattern: GET reads and draws one
        // button, POST confirms, so a mail scanner that opens every link confirms nobody.
        // Unsubscribe is RFC 8058 one-click: GET draws a button, POST unsubscribes, from that
        // button or a mail client's own. Neither POST checks antiforgery: the 32-byte token in the
        // path is the credential, and a forged post needs it. Pages are uncached and unindexed.
        app.MapGet("/r/confirm/{token}", ConfirmPromptAsync).AllowAnonymous();
        app.MapPost("/r/confirm/{token}", ConfirmAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/r/unsubscribe/{token}", UnsubscribePrompt).AllowAnonymous();
        app.MapPost("/r/unsubscribe/{token}", UnsubscribeAsync).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    public static RazorComponentResult<ReportPage> List(string? subscribed = null)
        => new(new { Subscribed = subscribed });

    public static RazorComponentResult<ReportPage> Month(string month, IReportLibrary reports, string? subscribed = null)
        => new(new { Month = month, Subscribed = subscribed })
        {
            StatusCode = reports.Get(month) is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK
        };

    /// <summary>
    /// Where the form's 303 goes: back to the page it was posted from, with the notice. Only a
    /// well-formed month is echoed into the address, so the form cannot redirect anywhere else.
    /// </summary>
    public static string AfterSubscribe(string? month, ReportSubscribeOutcome outcome)
    {
        var flag = outcome == ReportSubscribeOutcome.InvalidAddress ? "invalid" : "pending";
        var page = ReportMarkdown.IsMonth(month) ? $"/reports/{month}" : "/reports";
        return $"{page}?subscribed={flag}#report-subscribe";
    }

    private static async Task<IResult> SubscribeAsync([FromForm] string? email, [FromForm] string? month, HttpContext http,
        ReportSubscriptionService subscriptions, CancellationToken ct)
    {
        var outcome = await subscriptions.SubscribeAsync(email, ct);
        http.Response.Headers.Location = AfterSubscribe(month, outcome);
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static async Task<IResult> ConfirmPromptAsync(string token, HttpContext http, ReportSubscriptionService subscriptions, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        return await subscriptions.IsConfirmableAsync(token, ct)
            ? Page(ReportSubscriptionPageKind.ConfirmPrompt, token)
            : Page(ReportSubscriptionPageKind.ConfirmUnknown, statusCode: StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ConfirmAsync(string token, HttpContext http, ReportSubscriptionService subscriptions, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        return await subscriptions.ConfirmAsync(token, ct)
            ? Page(ReportSubscriptionPageKind.Confirmed)
            : Page(ReportSubscriptionPageKind.ConfirmUnknown, statusCode: StatusCodes.Status404NotFound);
    }

    private static IResult UnsubscribePrompt(string token, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Page(ReportSubscriptionPageKind.UnsubscribePrompt, token);
    }

    private static async Task<IResult> UnsubscribeAsync(string token, HttpContext http, ReportSubscriptionService subscriptions, CancellationToken ct)
    {
        await subscriptions.UnsubscribeAsync(token, ct);
        http.Response.Headers.CacheControl = "no-store";
        return Page(ReportSubscriptionPageKind.Unsubscribed);
    }

    private static RazorComponentResult<ReportSubscriptionPage> Page(ReportSubscriptionPageKind kind, string? token = null,
        int statusCode = StatusCodes.Status200OK)
        => new(new { Kind = kind, Token = token }) { StatusCode = statusCode };
}
