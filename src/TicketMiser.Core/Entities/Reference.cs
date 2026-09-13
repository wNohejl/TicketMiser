namespace TicketMiser.Core.Entities;

/// <summary>A kind of event: concert, sport, theatre. Keys follow SeatGeek's taxonomy names.</summary>
public class Category
{
    public int Id { get; set; }

    /// <summary>Stable slug used in config and source mappings, e.g. "concert".</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Follows configuration on every start; gates the desk's pickers.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>An artist, a team, a production: whoever the event is about.</summary>
public class Performer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Per-source identifiers, keyed by source key. Cross-source resolution lives here.</summary>
    public Dictionary<string, string> ExternalIds { get; set; } = [];
}

/// <summary>
/// Where an event happens. The ticketing provider is recorded because it decides which source
/// can see the primary market: an AXS room has no primary source we can read, and the board
/// must say so rather than show a resale number as if it were the face price.
/// </summary>
public class Venue
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "US";

    /// <summary>IANA zone, e.g. "America/Chicago". On-sale times are announced in it.</summary>
    public string? Timezone { get; set; }

    /// <summary>Who tickets the room at source: "ticketmaster", "axs", "etix", or null when unknown.</summary>
    public string? TicketingProvider { get; set; }

    public int? Capacity { get; set; }

    public Dictionary<string, string> ExternalIds { get; set; } = [];
}

public enum EventStatus
{
    Scheduled,
    Rescheduled,
    Postponed,
    Cancelled,
    Past
}

public class Event
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The public address of the on-sale record, <c>/e/{slug}</c>. Minted by <see cref="EventSlug"/>
    /// when the row is created and backfilled at startup for rows that predate it; null only
    /// between the two. Never rewritten once set: the link is the product.
    /// </summary>
    public string? Slug { get; set; }

    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public int? PerformerId { get; set; }
    public Performer? Performer { get; set; }

    public int VenueId { get; set; }
    public Venue? Venue { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Scheduled;

    /// <summary>
    /// When the public sale opens, as the primary source states it. Null when no source has said,
    /// or when the source says it is to be announced (<see cref="OnSaleTbd"/>).
    /// </summary>
    public DateTimeOffset? OnSaleAt { get; set; }

    public bool OnSaleTbd { get; set; }

    /// <summary>jsonb array of { name, startsAt, endsAt }: the presale windows the source lists.</summary>
    public string Presales { get; set; } = "[]";

    /// <summary>
    /// The source whose id this event was created from. Only that source may move
    /// <see cref="StartsAt"/> or <see cref="OnSaleAt"/>; every other source resolves onto the
    /// row and reports prices against it. The LineOps lesson: a resale site is not a schedule
    /// authority.
    /// </summary>
    public string? CreatedBySource { get; set; }

    public Dictionary<string, string> ExternalIds { get; set; } = [];
}

/// <summary>
/// Which market a source sells in. The board never ranks one against the other: a face-value
/// primary price and a resale ask are different products for the same seat (ADR 0011 restated).
/// </summary>
public enum SourceKind
{
    /// <summary>The box office: Ticketmaster, AXS. Face value, sometimes all-in.</summary>
    Primary,

    /// <summary>A marketplace: SeatGeek, StubHub. Asks, aggregated per event.</summary>
    Resale,

    /// <summary>A discovery-only source: the Discovery Feed. Names events and on-sale times, never prices.</summary>
    Feed
}

/// <summary>An external data provider. Rate limits and budgets are enforced against these rows.</summary>
public class Source
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }
    public string BaseUrl { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Published ceilings. Null means unmetered, which is a reason for more care rather than less.</summary>
    public int? RateLimitPerHour { get; set; }
    public int? RateLimitPerDay { get; set; }

    /// <summary>Monthly credit budget for credit-billed providers. None of the first two bill this way.</summary>
    public int? MonthlyCreditBudget { get; set; }

    /// <summary>Dev-only failure injection. Never enabled in production config.</summary>
    public string? FailureMode { get; set; }
}

/// <summary>
/// An event the operator is tracking. Watches are what the scheduler polls; nothing is fetched
/// unasked, which is the rule that keeps the budget guard a backstop rather than a routine.
/// </summary>
public class Watch
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>All-in price per ticket the operator wants to hear about. Null for "just watch".</summary>
    public decimal? TargetPrice { get; set; }

    public bool NotifyOnDrop { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
