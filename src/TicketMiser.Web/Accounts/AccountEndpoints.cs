using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TicketMiser.Core.Entities;
using TicketMiser.Reliability.Accounts;
using TicketMiser.Web.Components.Pages;
using TicketMiser.Web.Services;

namespace TicketMiser.Web.Accounts;

/// <summary>
/// Magic-link sign-in and the fan's own watches. No password anywhere: an address is posted,
/// a single-use link is emailed through the same notifier the alerts use, and following it
/// sets a cookie that names the account and nothing else.
///
/// <para>
/// Every page here is a static document (<see cref="AccountPage"/>) rendered by a
/// RazorComponentResult with no circuit, and none is cached. The two posts that change a
/// session (following a link, signing out) and the one that writes on the account's behalf
/// (watching an event) check an antiforgery token; the sign-in post does not, for the reason
/// the subscribe form does not: a forged post can only send a link to the address's owner.
/// The rate limit is what stops that ask being repeated at volume.
/// </para>
/// </summary>
public static class AccountEndpoints
{
    public const string SignInLimit = "sign-in";

    /// <summary>The cookie's name. It carries the account id and address, protected by Data Protection.</summary>
    public const string CookieName = "tm.account";

    /// <summary>
    /// Ten posts per client address per ten minutes, like the subscribe form: more than a
    /// person needs to sign in on a few devices, and less than a flood of sign-in emails.
    /// </summary>
    public static RateLimitPartition<string> SignInPartition(HttpContext http)
        => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 });

    /// <summary>Cookie authentication with no Identity and no password: HttpOnly, Lax, Secure over HTTPS, sliding thirty days.</summary>
    public static IServiceCollection AddAccountSignIn(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.LoginPath = "/account";
                options.LogoutPath = "/account/sign-out";
            });

        services.AddHttpContextAccessor();
        services.AddScoped<IOwnerContext, HttpOwnerContext>();
        services.AddSingleton<IAccountQueries, AccountQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account", AccountAsync).AllowAnonymous();

        app.MapPost("/account/sign-in", SignInAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(SignInLimit);

        // GET shows one button; POST spends the link. An email security scanner that opens
        // every link must not use up the only one a fan was sent, and the POST's antiforgery
        // token stops a forged post from signing a visitor in as someone else.
        app.MapGet("/account/verify/{token}", VerifyPromptAsync).AllowAnonymous();
        app.MapPost("/account/verify/{token}", VerifyAsync).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/account/sign-out", SignOutAsync).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/account/watches/{eventId:int}", WatchAsync).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    // The posts below validate the antiforgery token themselves rather than through form
    // binding: none of them binds a form field, so the framework would not check it for them.
    // DisableAntiforgery only turns off the middleware's automatic pass, not these checks.

    private static async Task<IResult> AccountAsync(HttpContext http, IAccountQueries accounts, string? sent, string? signedout, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (OwnerScope.From(http.User).AccountId is { } id)
        {
            if (await accounts.SummaryAsync(id, ct) is { } summary)
                return new RazorComponentResult<AccountPage>(new { Kind = AccountPageKind.SignedIn, Summary = summary });

            // A cookie for an account that no longer exists signs nobody in.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        var notice = sent switch
        {
            "1" => AccountNotice.LinkSent,
            "invalid" => AccountNotice.InvalidAddress,
            _ => signedout == "1" ? AccountNotice.SignedOut : AccountNotice.None
        };

        return new RazorComponentResult<AccountPage>(new { Kind = AccountPageKind.SignedOut, Notice = notice });
    }

    /// <summary>The same 303 for every address, known or not: the form reveals nobody's account.</summary>
    private static async Task<IResult> SignInAsync([FromForm] string? email, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        var outcome = await accounts.RequestSignInAsync(email, ct);
        return SeeOther(http, outcome == SignInRequestOutcome.InvalidAddress ? "/account?sent=invalid" : "/account?sent=1");
    }

    private static async Task<IResult> VerifyPromptAsync(string token, HttpContext http, AccountService accounts, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        return await accounts.IsRedeemableAsync(token, ct)
            ? new RazorComponentResult<AccountPage>(new { Kind = AccountPageKind.VerifyPrompt, Token = token })
            : new RazorComponentResult<AccountPage>(new { Kind = AccountPageKind.VerifyUnknown }) { StatusCode = StatusCodes.Status404NotFound };
    }

    private static async Task<IResult> VerifyAsync(string token, HttpContext http, IAntiforgery antiforgery, AccountService accounts, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest();

        if (await accounts.RedeemAsync(token, ct) is not { } account)
            return new RazorComponentResult<AccountPage>(new { Kind = AccountPageKind.VerifyUnknown }) { StatusCode = StatusCodes.Status404NotFound };

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Email, account.Email)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return SeeOther(http, "/account");
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest();

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SeeOther(http, "/account?signedout=1");
    }

    /// <summary>"Watch this event" on the record page: an owned watch, and the email that goes with it.</summary>
    private static async Task<IResult> WatchAsync(int eventId, HttpContext http, IAntiforgery antiforgery, AccountService accounts, CancellationToken ct)
    {
        if (OwnerScope.From(http.User).AccountId is not { } accountId)
            return SeeOther(http, "/account");

        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest();

        if (await accounts.GetAsync(accountId, ct) is null)
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return SeeOther(http, "/account");
        }

        if (await accounts.WatchAsync(accountId, eventId, ct) is not { } watched)
            return Results.NotFound();

        return SeeOther(http, $"/e/{Uri.EscapeDataString(watched.Slug ?? watched.EventId.ToString(CultureInfo.InvariantCulture))}");
    }

    private static IResult SeeOther(HttpContext http, string location)
    {
        http.Response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }
}
