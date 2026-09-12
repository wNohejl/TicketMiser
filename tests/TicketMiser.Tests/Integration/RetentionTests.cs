using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// The stream becomes one final price and is pruned; the on-sale record is untouched; a
/// purchase is graded only against the same kind of number.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RetentionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Final_price_is_the_newest_observation_at_or_before_start_and_the_record_survives()
    {
        var start = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var clock = new FakeClock(start + TimeSpan.FromHours(1));
        await using var db = fixture.CreateContext();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = new Source { Key = $"r-{suffix}", Name = "Resale", Kind = SourceKind.Resale };
        var venue = new Venue { Name = $"Venue {suffix}", City = "Nashville", State = "TN" };
        db.AddRange(source, venue);
        await db.SaveChangesAsync();

        var evt = new Event { Name = $"Show {suffix}", VenueId = venue.Id, StartsAt = start, OnSaleAt = start.AddDays(-30) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var run = new IngestionRun { SourceId = source.Id, JobKey = "test", StartedAt = clock.GetUtcNow(), Status = RunStatus.Success };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        db.PriceObservations.AddRange(
            new PriceObservation { EventId = evt.Id, SourceId = source.Id, ObservedAt = start.AddDays(-2), Lowest = 90m, AllIn = false, IngestionRunId = run.Id },
            new PriceObservation { EventId = evt.Id, SourceId = source.Id, ObservedAt = start.AddHours(-1), Lowest = 70m, AllIn = false, IngestionRunId = run.Id },
            // After the start: in-play, not the final.
            new PriceObservation { EventId = evt.Id, SourceId = source.Id, ObservedAt = start.AddMinutes(5), Lowest = 40m, AllIn = false, IngestionRunId = run.Id });

        db.OnSaleTicks.Add(new OnSaleTick { EventId = evt.Id, SourceId = source.Id, ObservedAt = start.AddDays(-30), MinutesFromOnSale = 0, Lowest = 88m, IngestionRunId = run.Id });

        db.Purchases.AddRange(
            new Purchase { EventId = evt.Id, SourceId = source.Id, Quantity = 2, PaidPerTicket = 65m, AllIn = false, PurchasedAt = start.AddDays(-10) },
            new Purchase { EventId = evt.Id, SourceId = source.Id, Quantity = 1, PaidPerTicket = 80m, AllIn = true, PurchasedAt = start.AddDays(-10) });
        await db.SaveChangesAsync();

        var retention = new RetentionService(db, Options.Create(new IngestionOptions()), clock, NullLogger<RetentionService>.Instance);
        var report = await retention.RunAsync();

        Assert.Equal(1, report.Promoted);
        var final = await db.FinalPrices.SingleAsync(f => f.EventId == evt.Id);
        Assert.Equal(70m, final.Lowest);
        Assert.Equal(start.AddHours(-1), final.ObservedAt);

        // The stream is gone, the record is not.
        Assert.Equal(0, await db.PriceObservations.CountAsync(o => o.EventId == evt.Id));
        Assert.Equal(1, await db.OnSaleTicks.CountAsync(t => t.EventId == evt.Id));

        // Face-value purchase graded against the face-value final; the all-in one is left alone.
        var purchases = await db.Purchases.Where(p => p.EventId == evt.Id).OrderBy(p => p.PaidPerTicket).ToListAsync();
        Assert.Equal(5m, purchases[0].SavingsVsFinal);
        Assert.NotNull(purchases[0].ResolvedAt);
        Assert.Null(purchases[1].SavingsVsFinal);
        Assert.Null(purchases[1].ResolvedAt);
        Assert.Equal(1, report.Graded);

        // A second pass has nothing to do.
        var again = await retention.RunAsync();
        Assert.Equal(0, again.Promoted);
    }
}
