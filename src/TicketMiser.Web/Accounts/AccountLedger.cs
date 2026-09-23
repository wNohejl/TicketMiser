using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Web.Services;

namespace TicketMiser.Web.Accounts;

/// <summary>
/// What /account draws of one account's purchases: each graded line, the savings with each fee
/// basis totalled apart, and a Log a purchase form for each watched event.
/// </summary>
/// <param name="Lines">The account's purchases, newest first, graded the way the desk grades them.</param>
/// <param name="Savings">The lines summarised by <see cref="PurchaseLedger.Summarise"/>: all-in and face value never added.</param>
/// <param name="Forms">One per watched event, in the watchlist's order, each with the sources a ticket could have come from.</param>
public sealed record AccountLedger(IReadOnlyList<PurchaseLine> Lines, SavingsSummary Savings, IReadOnlyList<PurchaseForm> Forms)
{
    public static AccountLedger Empty { get; } = new([], PurchaseLedger.Summarise([]), []);

    /// <summary>The events a receipt can be downloaded for: those with a purchase of the account's, newest purchase first.</summary>
    public IReadOnlyList<Event> ReceiptEvents => Lines
        .GroupBy(l => l.Event.Id)
        .Select(g => g.First().Event)
        .ToList();
}

/// <summary>
/// The account's purchases, read and written as that account and never as the operator.
///
/// <para>
/// Every call builds a <see cref="PurchaseQueries"/> fixed to <see cref="OwnerScope.ForAccount"/>
/// for the id the endpoint took from the cookie. It does not ask the request who is reading, so
/// nothing about the request — a missing claim, a background render — can widen it to the
/// operator's every-row scope. The ownership rule itself stays <see cref="Ownership"/>'s.
/// </para>
/// </summary>
public interface IAccountLedger
{
    Task<AccountLedger> LoadAsync(int accountId, IReadOnlyList<Event> watching, CancellationToken ct = default);

    /// <summary>The account's purchases on one event, oldest first; empty when it has none there.</summary>
    Task<IReadOnlyList<PurchaseLine>> ForEventAsync(int accountId, int eventId, CancellationToken ct = default);

    /// <summary>The form for one event, or null when no event has that id.</summary>
    Task<PurchaseForm?> FormAsync(int accountId, int eventId, CancellationToken ct = default);

    /// <inheritdoc cref="IPurchaseQueries.LogAsync"/>
    Task<Purchase> LogAsync(int accountId, PurchaseDraft draft, CancellationToken ct = default);

    /// <summary>False when the purchase is not the account's: another owner's row is never touched.</summary>
    Task<bool> DeleteAsync(int accountId, long purchaseId, CancellationToken ct = default);
}

public sealed class AccountLedgerService(IDbContextFactory<TicketMiserDbContext> factory, TimeProvider clock) : IAccountLedger
{
    private PurchaseQueries As(int accountId) => new(factory, clock, new FixedOwnerContext(OwnerScope.ForAccount(accountId)));

    public async Task<AccountLedger> LoadAsync(int accountId, IReadOnlyList<Event> watching, CancellationToken ct = default)
    {
        var queries = As(accountId);
        var lines = await queries.LoadAsync(ct);

        var forms = new List<PurchaseForm>(watching.Count);
        foreach (var evt in watching)
        {
            if (await queries.FormAsync(evt.Id, ct) is { } form)
                forms.Add(form);
        }

        return new AccountLedger(lines, PurchaseLedger.Summarise(lines), forms);
    }

    public Task<IReadOnlyList<PurchaseLine>> ForEventAsync(int accountId, int eventId, CancellationToken ct = default)
        => As(accountId).ForEventAsync(eventId, ct);

    public Task<PurchaseForm?> FormAsync(int accountId, int eventId, CancellationToken ct = default)
        => As(accountId).FormAsync(eventId, ct);

    public Task<Purchase> LogAsync(int accountId, PurchaseDraft draft, CancellationToken ct = default)
        => As(accountId).LogAsync(draft, ct);

    public Task<bool> DeleteAsync(int accountId, long purchaseId, CancellationToken ct = default)
        => As(accountId).DeleteAsync(purchaseId, ct);
}

/// <summary>What a fan typed into a Log a purchase form, kept as typed so a refused post can be drawn again.</summary>
/// <param name="AllIn"><c>true</c>, <c>false</c>, or anything else for unanswered. There is no default.</param>
/// <param name="SourceId">A source id, or empty for "Elsewhere".</param>
/// <param name="PurchasedAt">An HTML <c>datetime-local</c> value in Nashville time, or empty for now.</param>
public sealed record PurchaseFormInput(
    int EventId,
    string? Quantity,
    string? Price,
    string? AllIn,
    string? SourceId,
    string? PurchasedAt,
    string? Note)
{
    public static PurchaseFormInput Blank(int eventId) => new(eventId, "1", null, null, null, null, null);

    public static PurchaseFormInput From(IFormCollection form)
    {
        int.TryParse(form["eventId"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var eventId);

        return new PurchaseFormInput(
            eventId,
            form["quantity"].ToString(),
            form["price"].ToString(),
            form["allIn"].ToString(),
            form["sourceId"].ToString(),
            form["purchasedAt"].ToString(),
            form["note"].ToString());
    }
}

/// <summary>A form drawn again after a refused post: what was typed and what was wrong, keyed as <see cref="PurchaseLedger.Validate"/> keys it.</summary>
public sealed record PurchaseFormState(PurchaseFormInput Input, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Turns what the /account form posted into a <see cref="PurchaseDraft"/>. The ledger's own
/// validation decides what a purchase is; this adds only what text can get wrong before the
/// ledger sees it — a date that is not one, a source that is not on the form's list.
/// </summary>
public static class AccountPurchaseForm
{
    public const string DateTimeLocalFormat = "yyyy-MM-ddTHH:mm";

    public const string UnknownSource = "Choose where it was bought from the list, or Elsewhere.";

    /// <summary>The draft and every error, the ledger's included. The draft is a purchase only when the errors are empty.</summary>
    /// <param name="offered">The source ids the form offered; a post naming another is refused.</param>
    public static (PurchaseDraft Draft, IReadOnlyDictionary<string, string> Errors) Read(
        PurchaseFormInput input, IReadOnlyCollection<int> offered, DateTimeOffset now)
    {
        var parseErrors = new Dictionary<string, string>(StringComparer.Ordinal);

        int? quantity = int.TryParse(input.Quantity?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var q) ? q : null;

        // "$84.50" and "1,200" are what a person types from a receipt.
        var priceText = input.Price?.Trim().TrimStart('$').Trim();
        decimal? price = decimal.TryParse(priceText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var p) ? p : null;

        bool? allIn = input.AllIn switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };

        int? sourceId = null;
        if (input.SourceId is { Length: > 0 } sourceText)
        {
            if (int.TryParse(sourceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sid) && offered.Contains(sid))
                sourceId = sid;
            else
                parseErrors["source"] = UnknownSource;
        }

        DateTimeOffset? purchasedAt = null;
        if (input.PurchasedAt is { Length: > 0 } whenText)
        {
            if (DateTime.TryParseExact(whenText.Trim(), [DateTimeLocalFormat, DateTimeLocalFormat + ":ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                purchasedAt = FromNashville(local);
            else
                parseErrors["purchasedAt"] = "A date and time, or leave it empty for now.";
        }

        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note;
        var draft = new PurchaseDraft(input.EventId, quantity, price, allIn, sourceId, purchasedAt, note);

        var errors = new Dictionary<string, string>(PurchaseLedger.Validate(draft, now), StringComparer.Ordinal);
        foreach (var (key, message) in parseErrors)
            errors[key] = message;

        return (draft, errors);
    }

    /// <summary>A wall-clock time read in Nashville, where every room this product covers is.</summary>
    public static DateTimeOffset FromNashville(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(PurchaseReceipt.NashvilleZone);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(unspecified, TimeSpan.Zero);
        }
    }
}
