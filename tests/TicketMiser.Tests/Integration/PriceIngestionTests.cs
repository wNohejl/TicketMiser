using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Services;
using TicketMiser.Reliability;

namespace TicketMiser.Tests.Integration;

/// <summary>Store on change, the budget refusal, and the two kinds of zero.</summary>
[Collection(PostgresCollection.Name)]
public class PriceIngestionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

    private static PriceIngestionService Create(TicketMiserDbContext db, FakeClock clock)
        => new(db, new EventResolver(db),
            new CreditBudgetGuard(new BudgetCalculator(db), NullLogger<CreditBudgetGuard>.Instance),
            clock, NullLogger<PriceIngestionService>.Instance);

    private static async Task<(Source Row, StubPriceSource Stub, Event Event)> SeedAsync(
        TicketMiserDbContext db, FakeClock clock, int? perDay = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var row = new Source { Key = $"stub-{suffix}", Name = "Stub", Kind = SourceKind.Resale, RateLimitPerDay = perDay };
        db.Sources.Add(row);
        await db.SaveChangesAsync();

        var stub = new StubPriceSource(row.Key, SourceKind.Resale) { Clock = clock };
        var canonical = StubPriceSource.Bridgestone($"ev-{suffix}", $"Band {suffix}", Start);
        stub.Events.Add(canonical);

        var evt = await new EventResolver(db).ResolveEventAsync(row.Key, canonical, default);
        db.Watches.Add(new Watch { EventId = evt.Id, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();

        return (row, stub, evt);
    }

    [Fact]
    public async Task A_price_that_has_not_moved_is_not_written_and_a_move_is()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using var db = fixture.CreateContext();
        var (row, stub, evt) = await SeedAsync(db, clock);
        var service = Create(db, clock);

        var lowest = 68m;
        stub.Prices = (refs, now) => refs.Select(r =>
            new CanonicalPriceObservation(r.SourceEventId, now, "USD", lowest, 138m, 1250m, 412, false)).ToList();

        var refs = await service.WatchlistRefsAsync(stub, default);
        Assert.Single(refs);

        var first = await service.IngestAsync(stub, "watchlist:prices", refs, default);
        Assert.Equal(RunStatus.Success, first.Status);
        Assert.Equal(1, first.RowsIngested);

        clock.Advance(TimeSpan.FromMinutes(30));
        var quiet = await service.IngestAsync(stub, "watchlist:prices", refs, default);
        Assert.Equal(RunStatus.Success, quiet.Status);
        Assert.Equal(0, quiet.RowsIngested);

        clock.Advance(TimeSpan.FromMinutes(30));
        lowest = 64m;
        var moved = await service.IngestAsync(stub, "watchlist:prices", refs, default);
        Assert.Equal(1, moved.RowsIngested);

        var rows = await db.PriceObservations.Where(o => o.EventId == evt.Id).OrderBy(o => o.ObservedAt).ToListAsync();
        Assert.Equal([68m, 64m], rows.Select(r => r.Lowest!.Value));
        Assert.All(rows, r => Assert.False(r.AllIn));
    }

    [Fact]
    public async Task A_run_is_refused_rather_than_overrunning_the_daily_ceiling()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using var db = fixture.CreateContext();
        var (row, stub, _) = await SeedAsync(db, clock, perDay: 3);
        var service = Create(db, clock);

        // Three requests already spent today.
        db.IngestionRuns.Add(new IngestionRun
        {
            SourceId = row.Id,
            JobKey = "watchlist:prices",
            StartedAt = clock.GetUtcNow().AddHours(-1),
            FinishedAt = clock.GetUtcNow().AddHours(-1),
            Status = RunStatus.Success,
            RequestsMade = 3
        });
        await db.SaveChangesAsync();

        var refs = await service.WatchlistRefsAsync(stub, default);
        var outcome = await service.IngestAsync(stub, "watchlist:prices", refs, default);

        Assert.Equal(RunStatus.Partial, outcome.Status);
        Assert.Contains("budget", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stub.PriceCalls);
    }

    [Fact]
    public async Task A_provider_that_answers_for_nothing_is_partial_not_quiet()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        await using var db = fixture.CreateContext();
        var (row, stub, _) = await SeedAsync(db, clock);
        var service = Create(db, clock);

        // The stub forgets every event: the shape of a silent upstream break.
        stub.Events.Clear();
        stub.Prices = (_, _) => [];

        var refs = await service.WatchlistRefsAsync(stub, default);
        var outcome = await service.IngestAsync(stub, "watchlist:prices", refs, default);

        Assert.Equal(RunStatus.Partial, outcome.Status);
        Assert.Contains("no prices", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }
}
