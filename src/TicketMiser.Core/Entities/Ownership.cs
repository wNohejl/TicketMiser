using System.Globalization;
using System.Security.Claims;

namespace TicketMiser.Core.Entities;

/// <summary>
/// Whose rows a reader sees. Either the operator — the desk's user, who owns every row with no
/// <c>OwnerId</c> and reads every row there is — or one signed-in account, which reads only
/// the rows it owns.
///
/// <para>
/// The operator is who nobody-signed-in is. That is the single-operator deployment, and it
/// keeps working unchanged: every row written before accounts existed has no owner, and the
/// desk, with no sign-in of its own, reads them all. Exposing the desk publicly will need an
/// operator sign-in; until then it must not be exposed.
/// </para>
/// </summary>
public readonly record struct OwnerScope(int? AccountId)
{
    /// <summary>The operator: unfiltered reads, unowned writes.</summary>
    public static OwnerScope Operator => default;

    public static OwnerScope ForAccount(int accountId) => new(accountId);

    public bool IsOperator => AccountId is null;

    /// <summary>The <c>OwnerId</c> a row this reader writes carries.</summary>
    public int? OwnerId => AccountId;

    /// <summary>
    /// The scope a signed-in principal carries: its account id claim, or the operator when the
    /// principal is anonymous or carries none.
    /// </summary>
    public static OwnerScope From(ClaimsPrincipal? user)
        => user?.Identity?.IsAuthenticated == true
           && user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } value
           && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? ForAccount(id)
            : Operator;
}

/// <summary>
/// The one place the ownership rule lives. Every read of watches or purchases on behalf of a
/// person goes through <see cref="VisibleTo(IQueryable{Watch}, OwnerScope)"/>; nothing else
/// writes <c>OwnerId ==</c> into a query.
/// </summary>
public static class Ownership
{
    /// <summary>Watches this reader may read: all of them for the operator, its own for an account.</summary>
    public static IQueryable<Watch> VisibleTo(this IQueryable<Watch> watches, OwnerScope owner)
        => owner.AccountId is { } id ? watches.Where(w => w.OwnerId == id) : watches;

    /// <summary>Purchases this reader may read: all of them for the operator, its own for an account.</summary>
    public static IQueryable<Purchase> VisibleTo(this IQueryable<Purchase> purchases, OwnerScope owner)
        => owner.AccountId is { } id ? purchases.Where(p => p.OwnerId == id) : purchases;

    /// <summary>
    /// Watches this reader owns — the row a "watch" writes to. The operator's are the unowned
    /// ones, so the operator re-watching an event finds its own row and not a fan's.
    /// </summary>
    public static IQueryable<Watch> OwnedBy(this IQueryable<Watch> watches, OwnerScope owner)
        => owner.AccountId is { } id ? watches.Where(w => w.OwnerId == id) : watches.Where(w => w.OwnerId == null);
}

/// <summary>
/// Who is reading, for a service that reads on a person's behalf. The web host resolves it from
/// the request or the circuit; a test hands it a fixed scope.
/// </summary>
public interface IOwnerContext
{
    ValueTask<OwnerScope> GetAsync(CancellationToken ct = default);
}

/// <summary>A reader fixed at construction: a test's, or a background job's (the operator).</summary>
public sealed class FixedOwnerContext(OwnerScope owner) : IOwnerContext
{
    public static FixedOwnerContext Operator { get; } = new(OwnerScope.Operator);

    public ValueTask<OwnerScope> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(owner);
}
