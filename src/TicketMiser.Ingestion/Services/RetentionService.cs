using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Turns the observation stream into one final price per event per source and then throws
/// the stream away. Promotion before pruning, so nothing is deleted until what replaces it
/// exists. The on-sale record is never touched: it is not working state, it is the evidence.
/// </summary>
public class RetentionService(
    TicketMiserDbContext db,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<RetentionService> logger)
{
    private readonly RetentionOptions _settings = options.Value.Retention;

    public async Task<RetentionReport> RunAsync(CancellationToken ct = default)
    {
        var promoted = await PromoteAsync(ct);
        var graded = await GradePurchasesAsync(ct);
        var pruned = await PruneAsync(ct);

        if (promoted > 0 || pruned > 0 || graded > 0)
            logger.LogInformation("Retention: {Promoted} final prices, {Graded} purchases graded, {Pruned} observations dropped",
                promoted, graded, pruned);

        return new RetentionReport(promoted, graded, pruned);
    }

    /// <summary>The newest observation at or before the event's start, per source, becomes the final price.</summary>
    public async Task<int> PromoteAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - _settings.FinaliseAfterStart;

        var started = await db.Events
            .Where(e => e.StartsAt <= cutoff)
            .Where(e => db.PriceObservations.Any(o => o.EventId == e.Id))
            .Select(e => new { e.Id, e.StartsAt })
            .Take(_settings.PromoteBatchSize)
            .ToListAsync(ct);

        var written = 0;

        foreach (var evt in started)
        {
            ct.ThrowIfCancellationRequested();

            var done = await db.FinalPrices.Where(f => f.EventId == evt.Id).Select(f => f.SourceId).ToListAsync(ct);

            var finals = await db.PriceObservations
                .Where(o => o.EventId == evt.Id && o.ObservedAt <= evt.StartsAt && !done.Contains(o.SourceId))
                .GroupBy(o => o.SourceId)
                .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
                .ToListAsync(ct);

            foreach (var o in finals)
            {
                db.FinalPrices.Add(new FinalPrice
                {
                    EventId = o.EventId,
                    SourceId = o.SourceId,
                    Currency = o.Currency,
                    Lowest = o.Lowest,
                    Average = o.Average,
                    Highest = o.Highest,
                    ListingCount = o.ListingCount,
                    AllIn = o.AllIn,
                    ObservedAt = o.ObservedAt,
                    PromotedAt = now
                });
                written++;
            }
        }

        await db.SaveChangesAsync(ct);
        return written;
    }

    /// <summary>Paid versus final, on the same market and the same all-in kind, or not at all.</summary>
    public async Task<int> GradePurchasesAsync(CancellationToken ct = default)
    {
        var pending = await db.Purchases
            .Include(p => p.Source)
            .Where(p => p.ResolvedAt == null && db.FinalPrices.Any(f => f.EventId == p.EventId))
            .ToListAsync(ct);

        var graded = 0;

        foreach (var purchase in pending)
        {
            var finals = await db.FinalPrices
                .Include(f => f.Source)
                .Where(f => f.EventId == purchase.EventId)
                .ToListAsync(ct);

            // Grade against the same market the ticket was bought in, when known; otherwise
            // against the cheapest final of the same all-in kind. The rule is the ledger's, so
            // the Purchases window shows the final this run graded against. A final with no
            // price is never the comparison: it would resolve the purchase with no saving.
            var sources = finals.Where(f => f.Source is not null).Select(f => f.Source!).DistinctBy(s => s.Id).ToDictionary(s => s.Id);
            var comparable = PurchaseLedger.ComparableFinal(purchase, purchase.Source?.Kind, finals, sources);

            if (comparable is null)
                continue;

            purchase.SavingsVsFinal = PriceComparison.SavingsVsFinal(
                purchase.PaidPerTicket, purchase.AllIn, comparable.Lowest, comparable.AllIn);
            purchase.ResolvedAt = clock.GetUtcNow();
            graded++;
        }

        await db.SaveChangesAsync(ct);
        return graded;
    }

    /// <summary>Drops observations for (event, source) pairs whose final price is on record.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
        => await db.PriceObservations
            .Where(o => db.FinalPrices.Any(f => f.EventId == o.EventId && f.SourceId == o.SourceId))
            .ExecuteDeleteAsync(ct);

    /// <summary>
    /// Drops observation partitions that no longer hold anything. Only the stream's table;
    /// the on-sale record keeps every month it has ever written.
    /// </summary>
    public async Task<int> DropEmptyPartitionsAsync(CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE
                part record;
                keep_from date := date_trunc('month', now())::date;
                n bigint;
            BEGIN
                FOR part IN
                    SELECT c.relname AS name
                    FROM pg_inherits i
                    JOIN pg_class c ON c.oid = i.inhrelid
                    JOIN pg_class p ON p.oid = i.inhparent
                    WHERE p.relname = 'PriceObservations'
                      AND c.relname ~ '^PriceObservations_[0-9][0-9][0-9][0-9]_[0-9][0-9]$'
                LOOP
                    CONTINUE WHEN to_date(right(part.name, 7), 'YYYY_MM') >= keep_from;
                    EXECUTE format('SELECT count(*) FROM %I', part.name) INTO n;
                    CONTINUE WHEN n > 0;
                    EXECUTE format('DROP TABLE %I', part.name);
                END LOOP;
            END $$;
            """, ct);
}

public record RetentionReport(int Promoted, int Graded, int Pruned);
