using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Incidents window: the list, and the RCA behind one incident.
/// </summary>
public class IncidentsPanelTests : OperationsPanelBench
{
    [Fact]
    public void An_empty_record_explains_where_incidents_come_from_and_offers_the_Ops_window()
    {
        Services.AddSingleton<IIncidentQueries>(new FakeIncidentQueries());

        var cut = Render<IncidentsPanel>();

        var empty = cut.Find(".empty--new");

        Assert.Contains("No incidents recorded", empty.TextContent);
        Assert.Equal("Open Ops", cut.Find(".empty__action").TextContent.Trim());
    }

    [Fact]
    public void Opening_on_an_incident_lands_on_its_RCA_with_the_runbook_for_its_rule()
    {
        var incident = new Incident
        {
            Id = 3,
            Title = "Ticketmaster: no successful ingestion for 30.0h (SLO 26h).",
            Severity = AlertSeverity.Critical,
            Status = IncidentStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Timeline = """[{"At":"2026-09-12T08:00:00+00:00","Note":"Incident opened from alert [freshness]."}]""",
            Alerts = [new Alert { Id = 9, RuleKey = "freshness", Severity = AlertSeverity.Critical }]
        };

        Services.AddSingleton<IIncidentQueries>(new FakeIncidentQueries([incident]));

        var cut = Render<IncidentsPanel>(p => p.Add(x => x.IncidentId, 3));

        Assert.Single(cut.FindAll(".runbook"));
        Assert.Contains("Incident opened from alert", cut.Markup);
        Assert.Contains("Resolve incident", cut.Markup);
    }
}
