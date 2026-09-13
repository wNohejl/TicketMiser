using MudBlazor;
using TicketMiser.Desk;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;

namespace TicketMiser.Web.Windowing;

/// <summary>
/// Every window the desk can open, and the named workspaces that arrange them.
///
/// <para>
/// A new window is one entry here — the strip, the drawers and the host all read this list,
/// so nothing else needs touching. Panels are ordinary components and know nothing about
/// being windowed. Every entry points at <see cref="Placeholder"/> until its panel exists;
/// the catalogue is the plan made concrete first, so the desk can be driven before any
/// window has content. See the reuse plan, section 5, for what each one is meant to show.
/// </para>
/// </summary>
public static class WindowCatalog
{
    // Data: what is being tracked, and what it costs now.
    public const string Watchlist = "watchlist";
    public const string PriceHistory = "history";
    public const string Performers = "performers";

    // Analytics: what was paid, against what it would have cost.
    public const string Purchases = "purchases";
    public const string Savings = "savings";

    // Operations: whether the feeds are healthy, and what broke.
    public const string Ops = "ops";
    public const string Incidents = "incidents";
    public const string Runs = "runs";
    public const string Backfill = "backfill";

    // System.
    public const string Desk = "desk";

    // Destinations: about a subject, reached by following a name.
    public const string Event = "event";
    public const string Performer = "performer";
    public const string Venue = "venue";

    // The watchlist's follow-ups. Each takes an EventId, so several can be open at once.
    public const string AllSources = "sources";
    public const string LogPurchase = "purchase";
    public const string Trend = "trend";

    public static readonly IReadOnlyList<WindowDefinition> All =
    [
        new()
        {
            Key = Watchlist,
            Title = "Watchlist",
            Icon = Icons.Material.Filled.Leaderboard,
            Group = "Data",
            ComponentType = typeof(WatchlistPanel),
            Description = "Every watched event, the best price on it now, the source holding it, and how far the rest are behind.",
            DefaultWeight = 1.9,
            MinWidth = 680
        },
        new()
        {
            Key = PriceHistory,
            Title = "Price history",
            Icon = Icons.Material.Filled.ShowChart,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Description = "The lowest price per source over time, with the event and any purchase marked.",
            // Two charts side by side is the point of a chart.
            Singleton = false,
            DefaultWeight = 1.3
        },
        new()
        {
            Key = Performers,
            Title = "Performers",
            Icon = Icons.Material.Filled.People,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Description = "Artists, teams and productions; open one to see its events."
        },
        new()
        {
            Key = Purchases,
            Title = "Purchases",
            Icon = Icons.Material.Filled.Receipt,
            Group = "Analytics",
            ComponentType = typeof(Placeholder),
            Description = "What was paid, where, and how it compares with the price on the day."
        },
        new()
        {
            Key = Savings,
            Title = "Savings",
            Icon = Icons.Material.Filled.Savings,
            Group = "Analytics",
            ComponentType = typeof(Placeholder),
            Description = "Paid against day-of prices, by source and by category."
        },
        new()
        {
            Key = Ops,
            Title = "Ops",
            Icon = Icons.Material.Filled.MonitorHeart,
            Group = "Operations",
            ComponentType = typeof(OpsPanel),
            Description = "Source health, the allowance each feed has left, and the open alerts.",
            DefaultWeight = 1.4
        },
        new()
        {
            Key = Incidents,
            Title = "Incidents",
            Icon = Icons.Material.Filled.Warning,
            Group = "Operations",
            ComponentType = typeof(IncidentsPanel),
            Description = "What broke, and the write-up that closes it."
        },
        new()
        {
            Key = Runs,
            Title = "Runs",
            Icon = Icons.Material.Filled.PlayArrow,
            Group = "Operations",
            ComponentType = typeof(RunsPanel),
            Description = "Every fetch, what it cost, and what it wrote."
        },
        new()
        {
            Key = Backfill,
            Title = "History",
            Icon = Icons.Material.Filled.History,
            Group = "Operations",
            ComponentType = typeof(HistoryPanel),
            Description = "Walk past events for their final prices, where a source still offers them."
        },
        new()
        {
            Key = Desk,
            Title = "Desk settings",
            Icon = Icons.Material.Filled.Tune,
            Group = "System",
            ComponentType = typeof(DeskSettingsPanel),
            Description = "Appearance — theme, accent, text size and scale — then the window ceiling, the primary window and its share, and resolution.",
            MinWidth = 360,
            Shortcut = ","
        },

        // Destinations.
        new()
        {
            Key = Event,
            RequiresSubject = true,
            ReachedBy = "Open a row on the Watchlist.",
            Title = "Event",
            Icon = Icons.Material.Filled.Event,
            Group = "Data",
            ComponentType = typeof(EventPanel),
            Singleton = false,
            Description = "One event: every source's price now, its history, and the purchases logged against it."
        },
        new()
        {
            Key = Performer,
            RequiresSubject = true,
            ReachedBy = "Follow a performer's name from the Watchlist or Performers.",
            Title = "Performer",
            Icon = Icons.Material.Filled.Person,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Singleton = false,
            Description = "One performer's upcoming events and how their prices have run."
        },
        new()
        {
            Key = Venue,
            RequiresSubject = true,
            ReachedBy = "Follow a venue's name from an Event.",
            Title = "Venue",
            Icon = Icons.Material.Filled.Place,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Singleton = false,
            Description = "One venue's calendar and what its events have cost."
        },

        // Follow-ups.
        new()
        {
            Key = AllSources,
            RequiresSubject = true,
            ReachedBy = "Open a row on the Watchlist and press All sources.",
            Title = "All sources",
            Icon = Icons.Material.Filled.ViewList,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Singleton = false,
            Description = "Every source's number for one event, primary and resale badged apart."
        },
        new()
        {
            Key = LogPurchase,
            RequiresSubject = true,
            ReachedBy = "Open a row on the Watchlist and press Log purchase.",
            Title = "Log purchase",
            Icon = Icons.Material.Filled.Bolt,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Singleton = false,
            Description = "Record what you paid, prefilled from the price shown."
        },
        new()
        {
            Key = Trend,
            RequiresSubject = true,
            ReachedBy = "Open a row on the Watchlist and press Trend.",
            Title = "Trend",
            Icon = Icons.Material.Filled.TrendingUp,
            Group = "Data",
            ComponentType = typeof(Placeholder),
            Singleton = false,
            Description = "The last weeks of one event's lowest price, at a glance."
        }
    ];

    public static WindowDefinition? Find(string key) => All.FirstOrDefault(w => w.Key == key);

    public static readonly IReadOnlyList<Workspace> Workspaces =
    [
        new(
            "Morning triage",
            "Health first, then what broke, then the runs behind it.",
            [Ops, Incidents, Runs]),
        new(
            "Price watch",
            "The watchlist beside a history chart.",
            [Watchlist, PriceHistory]),
        new(
            "Review",
            "Purchases against the prices they were made at.",
            [Purchases, Savings])
    ];
}

/// <summary>
/// The catalogue as the desk reads it. The static class above is the application's own
/// vocabulary — its keys are referenced by name — and this is the one place it is handed
/// to the desk, which knows nothing of those names.
/// </summary>
public sealed class AppWindowCatalog : IWindowCatalog
{
    public IReadOnlyList<WindowDefinition> All => WindowCatalog.All;

    public IReadOnlyList<Workspace> Workspaces => WindowCatalog.Workspaces;

    public string DefaultPrimary => WindowCatalog.Watchlist;

    public WindowDefinition? Find(string key) => WindowCatalog.Find(key);
}
