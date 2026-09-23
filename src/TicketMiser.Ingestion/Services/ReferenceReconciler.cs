using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Keeps the reference rows in step with what is actually configured, every start. A category
/// is enabled if the market names it; a source is enabled if its adapter was registered. A
/// source that drops out takes its open alerts with it, resolved rather than deleted.
/// </summary>
public sealed class ReferenceReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<ReferenceReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketMiserDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();

        await ReconcileAsync(db, registry.RegisteredKeys, options.Value.Market.CategoryKey, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task ReconcileAsync(
        TicketMiserDbContext db, IReadOnlyList<string> registeredKeys, string categoryKey, CancellationToken ct)
    {
        foreach (var category in await db.Categories.ToListAsync(ct))
            category.Enabled = string.Equals(category.Key, categoryKey, StringComparison.OrdinalIgnoreCase);

        var registered = registeredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = 0;

        foreach (var source in await db.Sources.ToListAsync(ct))
        {
            var enabled = registered.Contains(source.Key);
            if (source.Enabled == enabled)
                continue;

            source.Enabled = enabled;
            if (!enabled)
                retired++;
        }

        await db.SaveChangesAsync(ct);

        var disabledIds = await db.Sources.Where(s => !s.Enabled).Select(s => s.Id).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var open = await db.Alerts
            .Where(a => a.ResolvedAt == null && a.SourceId != null && disabledIds.Contains(a.SourceId.Value))
            .ToListAsync(ct);

        foreach (var alert in open)
            alert.ResolvedAt = now;

        await db.SaveChangesAsync(ct);

        if (retired > 0 || open.Count > 0)
            logger.LogInformation("Sources not configured: {Sources}; resolved {Alerts} alert(s)",
                string.Join(", ", await db.Sources.Where(s => !s.Enabled).Select(s => s.Key).ToListAsync(ct)),
                open.Count);
    }
}
