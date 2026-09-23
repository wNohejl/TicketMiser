using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Primitives;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The bench the purchase windows render on: a faked ledger, a faked alert that answers as the
/// test says, the window manager, the toasts, and a clock.
/// </summary>
public abstract class PurchaseBench : DeskTestContext
{
    protected static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    protected static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary, BaseUrl = "https://app.ticketmaster.com/discovery/v2/" };
    protected static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale, BaseUrl = "https://api.seatgeek.com/2/" };

    protected static readonly IReadOnlyDictionary<int, Source> Sources = new Dictionary<int, Source> { [1] = Ticketmaster, [2] = SeatGeek };

    protected FakePurchaseQueries Ledger { get; private set; } = default!;

    protected FakeAlerts Alerts { get; } = new();

    protected WindowManager Windows { get; } = new(new AppWindowCatalog());

    protected PurchaseBench()
    {
        Services.AddSingleton(Windows);
        Services.AddScoped<DeskToasts>();
        Services.AddSingleton<IDeskAlerts>(Alerts);
        Services.AddSingleton(TimeProvider.System);
    }

    protected void UseLedger(FakePurchaseQueries ledger)
    {
        Ledger = ledger;
        Services.AddSingleton<IPurchaseQueries>(ledger);
    }

    protected static Event Ryman(int id = 7, string name = "Example Tour", int daysFromNow = 30) => new()
    {
        Id = id,
        Name = name,
        Slug = "example-performer-ryman-auditorium",
        StartsAt = Now.AddDays(daysFromNow),
        OnSaleAt = Now.AddDays(-10),
        Venue = new Venue { Name = "Ryman Auditorium", City = "Nashville", State = "TN", Timezone = "America/Chicago" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    protected static Purchase Bought(long id, Event evt, Source? source, decimal paid, bool allIn, int quantity = 2, decimal? saved = null) => new()
    {
        Id = id,
        EventId = evt.Id,
        Event = evt,
        SourceId = source?.Id,
        Quantity = quantity,
        PaidPerTicket = paid,
        AllIn = allIn,
        PurchasedAt = Now.AddDays(-20),
        SavingsVsFinal = saved
    };

    protected static FinalPrice Final(Event evt, Source source, decimal lowest, bool allIn) => new()
    {
        EventId = evt.Id,
        SourceId = source.Id,
        Lowest = lowest,
        AllIn = allIn,
        Currency = "USD"
    };

    protected static PurchaseLine Line(Purchase purchase, Event evt, params FinalPrice[] finals)
        => PurchaseLedger.Line(purchase, evt, finals, Sources, Now);
}

/// <summary>Answers the purchase windows from memory and records what they asked it to do.</summary>
public sealed class FakePurchaseQueries(IReadOnlyList<PurchaseLine>? lines = null, PurchaseForm? form = null) : IPurchaseQueries
{
    private List<PurchaseLine> _lines = (lines ?? []).ToList();

    public List<PurchaseDraft> Logged { get; } = [];

    public List<long> Deleted { get; } = [];

    public Task<IReadOnlyList<PurchaseLine>> LoadAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PurchaseLine>>(_lines.ToList());

    public Task<IReadOnlyList<PurchaseLine>> ForEventAsync(int eventId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PurchaseLine>>(_lines.Where(l => l.Purchase.EventId == eventId).ToList());

    public Task<SavingsSummary> SavingsAsync(CancellationToken ct = default)
        => Task.FromResult(PurchaseLedger.Summarise(_lines));

    public Task<PurchaseForm?> FormAsync(int eventId, CancellationToken ct = default)
        => Task.FromResult(form is not null && form.Event.Id == eventId ? form : null);

    public Task<Purchase> LogAsync(PurchaseDraft draft, CancellationToken ct = default)
    {
        Logged.Add(draft);
        return Task.FromResult(PurchaseLedger.ToPurchase(draft, DateTimeOffset.UtcNow));
    }

    public Task DeleteAsync(long purchaseId, CancellationToken ct = default)
    {
        Deleted.Add(purchaseId);
        _lines = _lines.Where(l => l.Purchase.Id != purchaseId).ToList();
        return Task.CompletedTask;
    }
}

/// <summary>An alert that answers as the test says and remembers what it was asked.</summary>
public sealed class FakeAlerts : IDeskAlerts
{
    public bool Answer { get; set; }

    public List<(string Heading, string? Message, string ConfirmLabel, string? CancelLabel, bool Destructive)> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string heading, string? message = null, string confirmLabel = "OK", string? cancelLabel = "Cancel", bool destructive = false)
    {
        Asked.Add((heading, message, confirmLabel, cancelLabel, destructive));
        return Task.FromResult(Answer);
    }
}
