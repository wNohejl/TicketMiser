namespace TicketMiser.Ingestion.Configuration;

public class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Where and what. One city, on purpose; a second is a second configuration.</summary>
    public MarketOptions Market { get; set; } = new();

    /// <summary>How often the scheduler loop wakes to check for due jobs.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>When true the host runs discovery once at startup. Never the price jobs: those cost quota.</summary>
    public bool RunOnStartup { get; set; }

    public SourceOptions Ticketmaster { get; set; } = new();
    public SourceOptions TicketmasterInventory { get; set; } = new();
    public FeedOptions DiscoveryFeed { get; set; } = new();
    public SeatGeekOptions SeatGeek { get; set; } = new();

    public DiscoveryOptions Discovery { get; set; } = new();
    public PricePollingOptions PricePolling { get; set; } = new();
    public OnSaleWatchOptions OnSaleWatch { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
}

public class MarketOptions
{
    public string City { get; set; } = "Nashville";
    public string StateCode { get; set; } = "TN";
    public string CountryCode { get; set; } = "US";

    /// <summary>Our category key. Each adapter maps it to the provider's own word.</summary>
    public string CategoryKey { get; set; } = "concert";

    /// <summary>How far ahead discovery looks for events.</summary>
    public TimeSpan DiscoveryHorizon { get; set; } = TimeSpan.FromDays(120);
}

public class SourceOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>
    /// How this client identifies itself. HttpClient sends no User-Agent by default, and the
    /// ESPN outage in LineOps was exactly that. Empty falls back to the runtime's own token.
    /// </summary>
    public string? UserAgent { get; set; }

    public string EffectiveUserAgent
        => string.IsNullOrWhiteSpace(UserAgent) ? DefaultUserAgent : UserAgent!;

    public static string DefaultUserAgent => "TicketMiser/0.1 (.NET/" + Environment.Version.ToString(2) + ")";
}

public class SeatGeekOptions
{
    public bool Enabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? UserAgent { get; set; }

    public string EffectiveUserAgent
        => string.IsNullOrWhiteSpace(UserAgent) ? SourceOptions.DefaultUserAgent : UserAgent!;
}

/// <summary>
/// The Discovery Feed: a daily national file on the same key as the Discovery API, with on-sale
/// and presale dates and no prices. Costs no quota and real disk, so the disk is capped.
/// </summary>
public class FeedOptions
{
    public bool Enabled { get; set; }

    /// <summary>Refuse a download whose declared length exceeds this. The US file is large; the cap is the safety.</summary>
    public long MaxBytes { get; set; } = 400L * 1024 * 1024;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

public class DiscoveryOptions
{
    /// <summary>How often each discovery source is swept for new events and on-sale dates.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Who decides when the progression sweep spends quota.</summary>
public enum PricePollingMode
{
    /// <summary>Only when an operator presses <b>Prices now</b>. Nothing is spent unasked.</summary>
    Manual,

    /// <summary>The scheduler sweeps on the derived cadence as well as on request.</summary>
    Scheduled
}

/// <summary>
/// The safety margins around the derived cadence. The cadence itself is arithmetic in
/// <c>PricePollPlanner</c>: what the day has left, over the cost of one sweep, over the hours
/// that remain.
/// </summary>
public class PricePollingOptions
{
    public PricePollingMode Mode { get; set; } = PricePollingMode.Manual;

    public bool RunsUnattended => Mode == PricePollingMode.Scheduled;

    /// <summary>Held back from the pacing maths so a manual pull or an on-sale watch is never refused. Floor is 20.</summary>
    public int ReservePercent { get; set; } = 20;

    /// <summary>Never sweep faster than this. Ticketmaster refreshes price ranges hourly at most.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaximumInterval { get; set; } = TimeSpan.FromHours(3);
}

/// <summary>
/// The on-sale record's cadence: every <see cref="Tick"/> from <see cref="Lead"/> before the
/// announced on-sale until <see cref="Tail"/> after it, then hourly for <see cref="HourlyTail"/>.
/// Ticks are scheduled from the event's on-sale time, not from a timer, so a restart inside
/// the window resumes on the next mark.
/// </summary>
public class OnSaleWatchOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Lead { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan Tail { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan Tick { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan HourlyTail { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>When the observation stream becomes one final price and is pruned. Never touches the on-sale record.</summary>
public class RetentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Wait this long after the event's start before taking its final price.</summary>
    public TimeSpan FinaliseAfterStart { get; set; } = TimeSpan.FromMinutes(10);

    public int PromoteBatchSize { get; set; } = 250;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);
}
