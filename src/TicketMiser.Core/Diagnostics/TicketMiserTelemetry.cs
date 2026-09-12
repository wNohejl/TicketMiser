using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TicketMiser.Core.Diagnostics;

/// <summary>
/// The names everything else in the platform emits under.
///
/// This lives in Core and uses nothing but the BCL. <c>ActivitySource</c> and <c>Meter</c> are
/// framework types, so the libraries that do the work can be instrumented without any of them
/// taking a dependency on OpenTelemetry: emitting a span and choosing where spans go are
/// separate decisions, and only the host makes the second one.
/// </summary>
public static class TicketMiserTelemetry
{
    public const string ServiceNamespace = "ticketmiser";

    public const string ActivitySourceName = "TicketMiser";
    public const string MeterName = "TicketMiser";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Counters for work as it happens, as opposed to the gauges that sample state.</summary>
    public static class Instruments
    {
        public static readonly Counter<long> Runs = Meter.CreateCounter<long>(
            "ticketmiser.ingestion.runs",
            description: "Ingestion runs completed, tagged by source, job and terminal status.");

        public static readonly Counter<long> Rows = Meter.CreateCounter<long>(
            "ticketmiser.ingestion.rows",
            description: "Rows written by ingestion runs.");

        public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
            "ticketmiser.ingestion.requests",
            description: "Upstream HTTP requests made by ingestion runs.");

        public static readonly Counter<long> Credits = Meter.CreateCounter<long>(
            "ticketmiser.ingestion.credits",
            description: "Provider credits spent, for credit-billed providers.");

        public static readonly Counter<long> OnSaleTicks = Meter.CreateCounter<long>(
            "ticketmiser.onsale.ticks",
            description: "On-sale record ticks written, tagged by source.");
    }

    /// <summary>Tag keys, fixed in one place so a metric and the span beside it agree.</summary>
    public static class Tags
    {
        public const string Source = "ticketmiser.source";
        public const string Job = "ticketmiser.job";
        public const string Market = "ticketmiser.market";
        public const string Status = "ticketmiser.status";
        public const string Rule = "ticketmiser.rule";
        public const string Dimension = "ticketmiser.budget_dimension";
        public const string Event = "ticketmiser.event";
    }
}
