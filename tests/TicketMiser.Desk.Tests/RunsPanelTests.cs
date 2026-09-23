using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Runs window: every fetch, filtered by source, job and state.
/// </summary>
public class RunsPanelTests : OperationsPanelBench
{
    [Fact]
    public void No_runs_yet_is_said_as_a_first_fetch_still_to_come_not_as_a_filter_miss()
    {
        Services.AddSingleton<IRunQueries>(new FakeRunQueries());

        var cut = Render<RunsPanel>();

        var empty = cut.FindAll(".empty").Select(e => e.TextContent.Trim()).Single();

        Assert.StartsWith("No runs recorded yet", empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Arriving_from_a_source_filters_to_that_source()
    {
        var source = Ticketmaster();
        var queries = new FakeRunQueries(
            runs:
            [
                new IngestionRun { Id = 1, SourceId = 1, Source = source, JobKey = "onsale:watch", Status = RunStatus.Success, StartedAt = DateTimeOffset.UtcNow, RowsIngested = 4, RequestsMade = 1 },
                new IngestionRun { Id = 2, SourceId = 2, JobKey = "events:discover", Status = RunStatus.Failed, StartedAt = DateTimeOffset.UtcNow, Error = "HttpRequestException: 503" }
            ],
            sources: [source]);

        Services.AddSingleton<IRunQueries>(queries);

        var cut = Render<RunsPanel>(p => p.Add(x => x.SourceId, 1));

        Assert.Equal(1, queries.LastFilter?.SourceId);
        Assert.Contains("onsale:watch", cut.Markup);
        Assert.DoesNotContain("events:discover", cut.FindAll(".mud-table-body .mud-table-cell").Select(c => c.TextContent));
    }
}
