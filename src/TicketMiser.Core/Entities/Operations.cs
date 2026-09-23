namespace TicketMiser.Core.Entities;

public enum RunStatus
{
    Running,
    Success,
    Partial,
    Failed
}

/// <summary>
/// One execution of one source's ingestion job. Written on every run, success or failure:
/// this table is the raw feed for the entire reliability layer.
/// </summary>
public class IngestionRun
{
    public long Id { get; set; }

    public int SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>Which job produced this run, e.g. "watchlist:prices", "onsale:watch".</summary>
    public string JobKey { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public int RowsIngested { get; set; }
    public int RequestsMade { get; set; }

    /// <summary>Credits consumed for credit-billed providers.</summary>
    public int CreditsSpent { get; set; }

    public string? Error { get; set; }

    public TimeSpan? Duration => FinishedAt is null ? null : FinishedAt - StartedAt;
}

/// <summary>Daily KPI rollup per source. Primary key is (Day, SourceId).</summary>
public class KpiDaily
{
    public DateOnly Day { get; set; }
    public int SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>Minutes since the last successful run, sampled at rollup time.</summary>
    public double FreshnessMinutes { get; set; }

    /// <summary>Successful runs / total runs, 0..1.</summary>
    public double SuccessRate { get; set; }

    public int RowsIngested { get; set; }
    public int RunCount { get; set; }
    public int ApiCreditsUsed { get; set; }
    public int RequestsMade { get; set; }
}

public enum AlertSeverity
{
    Info,
    Warn,
    Critical
}

public class Alert
{
    public long Id { get; set; }

    /// <summary>Rule that produced this alert, e.g. "freshness", "primary_reappeared".</summary>
    public string RuleKey { get; set; } = string.Empty;

    public int? SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>
    /// The event a watch rule fired for. Null for the platform rules, which are about a source.
    /// Part of the alert's identity: two events dropping in price are two alerts, not one.
    /// </summary>
    public int? EventId { get; set; }
    public Event? Event { get; set; }

    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset TriggeredAt { get; set; }

    /// <summary>Set when the underlying condition clears. Open alerts have null.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public int? IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public bool IsOpen => ResolvedAt is null;
}

public enum IncidentStatus
{
    Open,
    Mitigated,
    Resolved
}

/// <summary>
/// A tracked operational event with a written root-cause analysis.
/// Discipline: every real ingestion failure gets one.
/// </summary>
public class Incident
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>jsonb array of { at, note }: the running timeline.</summary>
    public string Timeline { get; set; } = "[]";

    public string? RootCause { get; set; }
    public string? CorrectiveActions { get; set; }

    public List<Alert> Alerts { get; set; } = [];

    public TimeSpan? TimeToResolve => ResolvedAt is null ? null : ResolvedAt - OpenedAt;
}
