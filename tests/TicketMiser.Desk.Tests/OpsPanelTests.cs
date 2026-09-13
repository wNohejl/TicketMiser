using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Ingestion.Services;
using TicketMiser.Reliability;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Ops window: health, the allowance, and the open alerts.
///
/// <para>
/// The allowance section is the part that is new on this desk. Phase 3 asks that the window
/// show quota used, reserved and the next sweep, and those are the numbers an operator reads
/// before pressing anything that spends — so the tiles are pinned here by label and by value,
/// against a snapshot built by hand rather than a database.
/// </para>
/// </summary>
public class OpsPanelTests : OperationsPanelBench
{
    private IRenderedComponent<OpsPanel> Render(OpsSnapshot snapshot)
    {
        Services.AddSingleton<IOpsQueries>(new FakeOpsQueries(snapshot));
        return RenderComponent<OpsPanel>();
    }

    /// <summary>The value under a tile's label, found by the label's own words.</summary>
    private static string MetricValue(IRenderedFragment cut, string label)
    {
        var tile = cut.FindAll(".metric")
            .Single(m => m.QuerySelector(".metric__label")?.TextContent.Trim() == label);

        return tile.QuerySelector(".metric__value")!.TextContent.Trim();
    }

    [Fact]
    public void An_empty_desk_says_so_in_every_section()
    {
        var cut = Render(OpsSnapshot.Empty);

        var empties = cut.FindAll(".empty").Select(e => e.TextContent.Trim()).ToList();

        Assert.Contains("No sources registered.", empties);
        Assert.Contains(empties, e => e.StartsWith("No open alerts", StringComparison.Ordinal));

        // Nothing metered, so no allowance tiles: a tile reporting zero of nothing would be a
        // number that looks like arithmetic and is not.
        Assert.DoesNotContain(cut.FindAll(".metric__label"), l => l.TextContent.Trim() == "Reserved");
        Assert.Equal("0/0", MetricValue(cut, "Sources in SLO"));
    }

    [Fact]
    public void The_allowance_tiles_carry_quota_used_reserved_left_and_the_next_sweep()
    {
        var source = Ticketmaster(perDay: 5000);
        var usage = new BudgetUsage(
            RequestsLastHour: 40, RequestsLastDay: 1200, CreditsThisMonth: 0,
            HourlyLimit: 200, DailyLimit: 5000, MonthlyCreditLimit: null);

        // A fifth of the day held back, as the planner computes it: 5000 × 20% = 1000.
        var budget = new SourceBudget(source, usage, DailyLimit: 5000, UsedToday: 1200, Reserved: 1000, Left: 2800);

        var health = new SourceHealth(source, DateTimeOffset.UtcNow, 5, 1.0, 12, 300, 1.0, RunStatus.Success, null);

        var plan = new PollPlan(
            Interval: TimeSpan.FromMinutes(30), CallsPerSweep: 14, SweepsRemainingToday: 20,
            BoundBy: "ticketmaster", Watchlist: 14, OnSaleEventsToday: 2, OnSaleCallsReserved: 100, ReserveCalls: 1000);

        var lastSweep = DateTimeOffset.UtcNow.AddMinutes(-10);

        var snapshot = OpsSnapshot.Empty with
        {
            Health = [health],
            Budgets = [budget],
            Plan = plan,
            SweepsUnattended = true,
            LastSweepAt = lastSweep,
            CallsPerOnSaleEvent = 50
        };

        var cut = Render(snapshot);

        Assert.Equal("1,200/5,000", MetricValue(cut, "Used today"));
        Assert.Equal("1,000", MetricValue(cut, "Reserved"));
        Assert.Equal("2,800", MetricValue(cut, "Left to sweep"));

        // The next sweep is the last one plus the interval, read as a clock time.
        Assert.Equal((lastSweep + plan.Interval).ToLocalTime().ToString("HH:mm"), MetricValue(cut, "Next sweep"));

        // The on-sale cost is stated, per event and set aside in total.
        var text = cut.Markup;
        Assert.Contains("2</span> on-sale windows today", text);
        Assert.Contains(">50</span> calls each", text);
        Assert.Contains(">100</span> calls set aside", text);
    }

    [Fact]
    public void On_request_sweeps_report_no_clock_time_and_say_nothing_is_fetched_unasked()
    {
        var source = Ticketmaster(perDay: 5000);
        var usage = new BudgetUsage(0, 0, 0, 200, 5000, null);

        var snapshot = OpsSnapshot.Empty with
        {
            Health = [new SourceHealth(source, null, null, 1.0, 0, 0, null, null, null)],
            Budgets = [new SourceBudget(source, usage, 5000, 0, 1000, 4000)],
            Plan = new PollPlan(TimeSpan.FromMinutes(30), 3, 100, "ticketmaster", 3, 0, 0, 1000),
            SweepsUnattended = false
        };

        var cut = Render(snapshot);

        Assert.Equal("on request", MetricValue(cut, "Next sweep"));
        Assert.Contains("nothing is fetched unasked", cut.Markup);
    }

    [Fact]
    public void An_open_alert_shows_its_first_runbook_step_on_the_row()
    {
        var source = Ticketmaster();

        var alert = new Alert
        {
            Id = 7,
            RuleKey = AlertRules.Freshness,
            SourceId = source.Id,
            Source = source,
            Severity = AlertSeverity.Critical,
            Message = "Ticketmaster: no successful ingestion for 30.0h (SLO 26h).",
            TriggeredAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var cut = Render(OpsSnapshot.Empty with { Alerts = [alert] });

        // The runbook's first triage step for freshness sends the operator to the Runs window.
        var step = RunbookSteps.FirstStep(AlertRules.Freshness);

        Assert.NotNull(step);
        Assert.Contains("Runs", step);
        Assert.Contains(step, cut.Markup);

        // Critical drives the window's pulse and the alert count.
        Assert.Equal("1", MetricValue(cut, "Open alerts"));
    }
}
