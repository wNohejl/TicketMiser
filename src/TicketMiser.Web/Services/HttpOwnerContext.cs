using Microsoft.AspNetCore.Components.Authorization;
using TicketMiser.Core.Entities;

namespace TicketMiser.Web.Services;

/// <summary>
/// Who is reading, in the web host. A request that carries the account cookie reads as that
/// account; anything else — the desk with nobody signed in — reads as the operator, which is
/// what every reader was before accounts existed.
///
/// <para>
/// The request's user first: a static page, the receipt and the account endpoints all run in
/// a request, and a minimal endpoint has no authentication state for Blazor to hand over. A
/// circuit falls back to the state it opened with. Both read the same cookie, so the two
/// answers agree whenever both exist.
/// </para>
/// </summary>
public sealed class HttpOwnerContext(IHttpContextAccessor http, AuthenticationStateProvider? auth = null) : IOwnerContext
{
    public async ValueTask<OwnerScope> GetAsync(CancellationToken ct = default)
    {
        if (http.HttpContext is { } context)
            return OwnerScope.From(context.User);

        if (auth is not null)
            return OwnerScope.From((await auth.GetAuthenticationStateAsync()).User);

        return OwnerScope.Operator;
    }
}
