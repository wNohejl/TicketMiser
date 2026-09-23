using Microsoft.AspNetCore.Http.HttpResults;
using TicketMiser.Web.Components.Pages;

namespace TicketMiser.Web.Reports;

/// <summary>
/// GET /reports and GET /reports/{yyyy-mm}: the monthly Nashville reports, as static whole
/// documents (<see cref="ReportPage"/>), anonymous and output-cached like the calendar.
///
/// <para>
/// A month is served only when its file says <c>published: true</c>; an unpublished or
/// missing month is a 404, which the output cache does not keep. Publishing is a commit.
/// Nothing here mails a report to subscribers: that is not built.
/// </para>
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReports(this IEndpointRouteBuilder app, string cachePolicy)
    {
        app.MapGet("/reports", List).AllowAnonymous().CacheOutput(cachePolicy);
        app.MapGet("/reports/{month}", Month).AllowAnonymous().CacheOutput(cachePolicy);
        return app;
    }

    public static RazorComponentResult<ReportPage> List() => new();

    public static RazorComponentResult<ReportPage> Month(string month, IReportLibrary reports)
        => new(new { Month = month })
        {
            StatusCode = reports.Get(month) is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK
        };
}
