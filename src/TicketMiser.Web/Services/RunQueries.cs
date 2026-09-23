using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>The Runs window's filter. Nulls mean "any".</summary>
public record RunFilter(int? SourceId = null, RunStatus? Status = null, string? JobKey = null);

/// <summary>What the Runs panel reads. An interface so a render test can stand in for the database.</summary>
public interface IRunQueries
{
    Task<IReadOnlyList<Source>> SourcesAsync(CancellationToken ct = default);

    /// <summary>Every job key that has ever produced a run, so the filter offers only real ones.</summary>
    Task<IReadOnlyList<string>> JobKeysAsync(CancellationToken ct = default);

    Task<IReadOnlyList<IngestionRun>> RunsAsync(RunFilter filter, int take, CancellationToken ct = default);
}

public sealed class RunQueries(IDbContextFactory<TicketMiserDbContext> dbFactory) : IRunQueries
{
    public async Task<IReadOnlyList<Source>> SourcesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sources.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> JobKeysAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IngestionRuns.Select(r => r.JobKey).Distinct().OrderBy(k => k).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<IngestionRun>> RunsAsync(RunFilter filter, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.IngestionRuns.Include(r => r.Source).AsNoTracking().AsQueryable();

        if (filter.SourceId is { } sourceId)
            query = query.Where(r => r.SourceId == sourceId);

        if (filter.Status is { } status)
            query = query.Where(r => r.Status == status);

        if (filter.JobKey is { Length: > 0 } jobKey)
            query = query.Where(r => r.JobKey == jobKey);

        return await query.OrderByDescending(r => r.StartedAt).Take(take).ToListAsync(ct);
    }
}
