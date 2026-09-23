using Microsoft.Extensions.DependencyInjection;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Primitives;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The bench the operations windows render on.
///
/// <para>
/// Each panel reads through one small query interface rather than holding EF queries of its
/// own, which is what lets a render test hand it a snapshot and check the markup without a
/// database. The fakes here answer with nothing unless a test says otherwise, so the first
/// thing every panel is proven to do is explain an empty desk.
/// </para>
/// </summary>
public abstract class OperationsPanelBench : DeskTestContext
{
    protected OperationsPanelBench()
    {
        // The two desk services a panel injects: the window it sits in, and the toasts it
        // reports outcomes through. Neither is exercised here; both must resolve.
        Services.AddSingleton(new WindowManager(new AppWindowCatalog()));
        Services.AddScoped<DeskToasts>();
    }

    protected static Source Ticketmaster(int? perDay = 5000) => new()
    {
        Id = 1,
        Key = "ticketmaster",
        Name = "Ticketmaster",
        Kind = SourceKind.Primary,
        RateLimitPerDay = perDay,
        RateLimitPerHour = perDay is null ? null : 200
    };
}

/// <summary>Answers the Ops panel with whatever snapshot the test built, and records what it was asked to do.</summary>
public sealed class FakeOpsQueries(OpsSnapshot snapshot) : IOpsQueries
{
    public int Evaluations { get; private set; }

    public Task<OpsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(snapshot);

    public Task EvaluateAsync(CancellationToken ct = default)
    {
        Evaluations++;
        return Task.CompletedTask;
    }

    public Task<int> PromoteAsync(long alertId, CancellationToken ct = default) => Task.FromResult(1);
}

public sealed class FakeIncidentQueries(IReadOnlyList<Incident>? incidents = null) : IIncidentQueries
{
    private readonly IReadOnlyList<Incident> _incidents = incidents ?? [];

    public Task<IReadOnlyList<Incident>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(_incidents);

    public Task<Incident?> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_incidents.FirstOrDefault(i => i.Id == id));

    public Task AddNoteAsync(int id, string note, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResolveAsync(int id, string rootCause, string correctiveActions, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetStatusAsync(int id, IncidentStatus status, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeRunQueries(IReadOnlyList<IngestionRun>? runs = null, IReadOnlyList<Source>? sources = null) : IRunQueries
{
    private readonly IReadOnlyList<IngestionRun> _runs = runs ?? [];
    private readonly IReadOnlyList<Source> _sources = sources ?? [];

    /// <summary>The filter the panel last asked with, so a test can check a shortcut landed.</summary>
    public RunFilter? LastFilter { get; private set; }

    public Task<IReadOnlyList<Source>> SourcesAsync(CancellationToken ct = default) => Task.FromResult(_sources);

    public Task<IReadOnlyList<string>> JobKeysAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(_runs.Select(r => r.JobKey).Distinct().ToList());

    public Task<IReadOnlyList<IngestionRun>> RunsAsync(RunFilter filter, int take, CancellationToken ct = default)
    {
        LastFilter = filter;

        IReadOnlyList<IngestionRun> matched = _runs
            .Where(r => filter.SourceId is null || r.SourceId == filter.SourceId)
            .Where(r => filter.Status is null || r.Status == filter.Status)
            .Where(r => filter.JobKey is null || r.JobKey == filter.JobKey)
            .Take(take)
            .ToList();

        return Task.FromResult(matched);
    }
}

public sealed class FakeHistoryQueries(HistorySummary? summary = null, IReadOnlyList<FinalPrice>? finals = null) : IHistoryQueries
{
    private readonly HistorySummary _summary = summary ?? new HistorySummary(0, 0, 0);
    private readonly IReadOnlyList<FinalPrice> _finals = finals ?? [];

    public Task<HistorySummary> SummaryAsync(CancellationToken ct = default) => Task.FromResult(_summary);

    public Task<IReadOnlyList<FinalPrice>> RecentFinalsAsync(int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FinalPrice>>(_finals.Take(take).ToList());
}
