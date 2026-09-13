using TicketMiser.Core.Entities;
using TicketMiser.Reliability;

namespace TicketMiser.Web.Services;

/// <summary>What the Incidents panel reads and does. An interface so a render test can stand in for the database.</summary>
public interface IIncidentQueries
{
    Task<IReadOnlyList<Incident>> GetAllAsync(CancellationToken ct = default);

    Task<Incident?> GetAsync(int id, CancellationToken ct = default);

    Task AddNoteAsync(int id, string note, CancellationToken ct = default);

    /// <summary>Throws <see cref="ArgumentException"/> when either field is blank — the panel shows that inline.</summary>
    Task ResolveAsync(int id, string rootCause, string correctiveActions, CancellationToken ct = default);

    Task SetStatusAsync(int id, IncidentStatus status, CancellationToken ct = default);
}

/// <summary>A scope per call over <see cref="IncidentService"/>, for the same reason <see cref="OpsQueries"/> does it.</summary>
public sealed class IncidentQueries(IServiceScopeFactory scopeFactory) : IIncidentQueries
{
    public async Task<IReadOnlyList<Incident>> GetAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IncidentService>().GetAllAsync(ct);
    }

    public async Task<Incident?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IncidentService>().GetAsync(id, ct);
    }

    public async Task AddNoteAsync(int id, string note, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IncidentService>().AddNoteAsync(id, note, ct);
    }

    public async Task ResolveAsync(int id, string rootCause, string correctiveActions, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IncidentService>().ResolveAsync(id, rootCause, correctiveActions, ct);
    }

    public async Task SetStatusAsync(int id, IncidentStatus status, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IncidentService>().SetStatusAsync(id, status, ct);
    }
}
