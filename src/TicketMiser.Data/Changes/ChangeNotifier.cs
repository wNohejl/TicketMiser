using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TicketMiser.Core.Entities;

namespace TicketMiser.Data.Changes;

/// <summary>
/// What changed, named coarsely enough that a window can decide whether it cares.
/// </summary>
public static class DataTopics
{
    /// <summary>The Postgres channel every change is announced on.</summary>
    public const string Channel = "ticketmiser_changes";

    /// <summary>Events and what they are about: performers, venues, categories.</summary>
    public const string Events = "events";

    /// <summary>Quotes, on-sale ticks and final prices — what a poll run writes.</summary>
    public const string Prices = "prices";

    public const string Watches = "watches";
    public const string Purchases = "purchases";
    public const string Alerts = "alerts";
    public const string Runs = "runs";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Events, Prices, Watches, Purchases, Alerts, Runs };

    /// <summary>
    /// The topic an entity's rows belong to, or null for rows no window draws from: sources,
    /// accounts, sign-in tokens, fan subscriptions and deliveries.
    /// </summary>
    public static string? For(Type entity) => entity.Name switch
    {
        nameof(Event) or nameof(Performer) or nameof(Venue) or nameof(Category) => Events,
        nameof(PriceObservation) or nameof(OnSaleTick) or nameof(FinalPrice) => Prices,
        nameof(Watch) => Watches,
        nameof(Purchase) => Purchases,
        nameof(Alert) or nameof(Incident) => Alerts,
        nameof(IngestionRun) or nameof(KpiDaily) => Runs,
        _ => null
    };
}

/// <summary>
/// Announces every save on <see cref="DataTopics.Channel"/>, naming the topics it touched.
///
/// <para>
/// Without it no window refreshes itself: a quote the worker's poll wrote reaches an open
/// Watchlist whenever the fan next clicks. One interceptor on the context covers every writer at
/// once — the worker's scheduler, the web host's own when it runs one, the purchase form, the
/// reliability evaluator — without any of them having to remember to say so, and <c>NOTIFY</c>
/// crosses the process boundary that an in-memory event cannot.
/// </para>
///
/// <para>
/// Sent after the save, on the context's own connection. <c>NOTIFY</c> is transactional: inside
/// one of the initializer's explicit transactions it is held until commit and dropped on
/// rollback, so a notice only ever describes committed rows. Best-effort by design: a notice that
/// fails to send costs a refresh, which the listener's fallback poll makes up, and must never
/// fail the save it describes. Bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> calls do not pass
/// through here; the desk's two (stopping a watch, deleting a purchase) reload the window that
/// made them, and retention prunes rows no window is waiting on.
/// </para>
/// </summary>
public sealed class ChangeNotifier(ILogger<ChangeNotifier> logger) : SaveChangesInterceptor
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, HashSet<string>> _pending = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await AnnounceAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AnnounceAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // Nothing was written, so there is nothing to announce; a retry collects afresh.
        if (eventData.Context is { } context)
            _pending.Remove(context);

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            _pending.Remove(context);

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
            return;

        var topics = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => DataTopics.For(e.Metadata.ClrType))
            .OfType<string>()
            .ToHashSet();

        if (topics.Count > 0)
            _pending.AddOrUpdate(context, topics);
    }

    private async Task AnnounceAsync(DbContext? context, CancellationToken ct)
    {
        if (context is null || !_pending.TryGetValue(context, out var topics))
            return;

        _pending.Remove(context);

        // The raw connection rather than Database.ExecuteSql: this runs inside SaveChanges, and
        // a second EF operation on the same context here would trip its concurrency guard.
        var connection = context.Database.GetDbConnection();
        var opened = false;

        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
                opened = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT pg_notify('{DataTopics.Channel}', @topics)";

            // Enlisted in the context's transaction when there is one, so the notice waits for it.
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            var parameter = command.CreateParameter();
            parameter.ParameterName = "topics";
            parameter.Value = string.Join(',', topics.Order());
            command.Parameters.Add(parameter);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Change notice for {Topics} not sent", string.Join(',', topics));
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }
}
