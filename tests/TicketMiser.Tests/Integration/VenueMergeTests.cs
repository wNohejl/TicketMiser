using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// The startup repair for rooms the resolver split before it learned SeatGeek's town suffix:
/// "The Truth" and "The Truth - Nashville" become one room, each show at them one event, and
/// nothing that pointed at the duplicate is lost.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VenueMergeTests(PostgresFixture fixture)
{
    // Well after the other suites' nights, so a retention pass elsewhere never promotes these.
    private static readonly DateTimeOffset Start = new(2027, 3, 6, 1, 0, 0, TimeSpan.Zero);

    private sealed record Scene(
        string Suffix, Source Primary, Source Resale, Venue Kept, Venue DuplicateRoom,
        Event Twin, Event Duplicate, Event Lone, long RunId);

    private static async Task<Scene> SetUpAsync(TicketMiserDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var primary = new Source { Key = $"primary-{suffix}", Name = "Primary", Kind = SourceKind.Primary };
        var resale = new Source { Key = $"resale-{suffix}", Name = "Resale", Kind = SourceKind.Resale };
        var performer = new Performer { Name = $"Acid Bath {suffix}" };
        db.AddRange(primary, resale, performer);
        await db.SaveChangesAsync();

        var kept = new Venue
        {
            Name = $"The Truth {suffix}",
            City = "Nashville",
            State = "TN",
            Timezone = "America/Chicago",
            ExternalIds = new() { [primary.Key] = $"tm-v-{suffix}" }
        };
        db.Venues.Add(kept);
        await db.SaveChangesAsync();

        var duplicate = new Venue
        {
            Name = $"The Truth {suffix} - Nashville",
            City = "Nashville",
            State = "TN",
            ExternalIds = new() { [resale.Key] = $"sg-v-{suffix}" },
            Capacity = 500
        };
        db.Venues.Add(duplicate);
        await db.SaveChangesAsync();

        var twin = new Event
        {
            Name = "ACID BATH with JEFF THE BROTHERHOOD",
            Slug = $"acid-bath-the-truth-{suffix}",
            VenueId = kept.Id,
            PerformerId = performer.Id,
            StartsAt = Start,
            CreatedBySource = primary.Key,
            ExternalIds = new() { [primary.Key] = $"tm-{suffix}" }
        };
        var dup = new Event
        {
            Name = "Acid Bath with JEFF the Brotherhood",
            Slug = $"acid-bath-the-truth-nashville-{suffix}",
            VenueId = duplicate.Id,
            PerformerId = performer.Id,
            StartsAt = Start.AddMinutes(30),
            OnSaleAt = Start.AddDays(-30),
            CreatedBySource = resale.Key,
            // SeatGeek's claim about the Ticketmaster id: the legacy host id, never the
            // Discovery id the twin carries. It must not keep the two apart.
            ExternalIds = new() { [resale.Key] = $"sg-{suffix}", [primary.Key] = $"1B00{suffix}" }
        };
        var lone = new Event
        {
            Name = $"Only SeatGeek {suffix}",
            Slug = $"only-seatgeek-{suffix}",
            VenueId = duplicate.Id,
            StartsAt = Start.AddDays(7),
            CreatedBySource = resale.Key,
            ExternalIds = new() { [resale.Key] = $"sg-lone-{suffix}" }
        };
        db.Events.AddRange(twin, dup, lone);
        await db.SaveChangesAsync();

        var run = new IngestionRun { SourceId = resale.Id, JobKey = "test", StartedAt = Start.AddDays(-31), Status = RunStatus.Success };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        return new Scene(suffix, primary, resale, kept, duplicate, twin, dup, lone, run.Id);
    }

    private static DatabaseInitializer Initializer(TicketMiserDbContext db)
        => new(db, NullLogger<DatabaseInitializer>.Instance);

    [Fact]
    public async Task A_split_room_becomes_one_room_and_its_twin_shows_one_event_and_a_second_run_does_nothing()
    {
        await using var db = fixture.CreateContext();
        var s = await SetUpAsync(db);

        db.Watches.Add(new Watch { EventId = s.Duplicate.Id, TargetPrice = 45m, CreatedAt = Start.AddDays(-40) });
        db.OnSaleTicks.Add(new OnSaleTick
        {
            EventId = s.Duplicate.Id,
            SourceId = s.Resale.Id,
            ObservedAt = Start.AddDays(-30),
            MinutesFromOnSale = 0,
            Lowest = 88m,
            IngestionRunId = s.RunId
        });
        db.PriceObservations.Add(new PriceObservation
        {
            EventId = s.Duplicate.Id,
            SourceId = s.Resale.Id,
            ObservedAt = Start.AddDays(-2),
            Lowest = 70m,
            AllIn = false,
            IngestionRunId = s.RunId
        });
        await db.SaveChangesAsync();

        var first = await Initializer(db).MergeDuplicateVenuesAsync();

        var counts = Assert.Single(first, c => c.Pair.KeptId == s.Kept.Id);
        Assert.Equal(s.DuplicateRoom.Id, counts.Pair.DuplicateId);
        Assert.Equal(1, counts.EventsMerged);
        Assert.Equal(1, counts.EventsMoved);
        Assert.Equal(1, counts.Watches);
        Assert.Equal(1, counts.Ticks);
        Assert.Equal(1, counts.Observations);

        db.ChangeTracker.Clear();

        // One room, carrying both sources' ids, the first name, and the duplicate's gaps.
        Assert.False(await db.Venues.AnyAsync(v => v.Id == s.DuplicateRoom.Id));
        var room = await db.Venues.SingleAsync(v => v.Id == s.Kept.Id);
        Assert.Equal($"The Truth {s.Suffix}", room.Name);
        Assert.Equal($"tm-v-{s.Suffix}", room.ExternalIds[s.Primary.Key]);
        Assert.Equal($"sg-v-{s.Suffix}", room.ExternalIds[s.Resale.Key]);
        Assert.Equal(500, room.Capacity);

        // The show is one event under the twin's id and address, known to both sources.
        Assert.False(await db.Events.AnyAsync(e => e.Id == s.Duplicate.Id));
        var show = await db.Events.SingleAsync(e => e.Id == s.Twin.Id);
        Assert.Equal($"acid-bath-the-truth-{s.Suffix}", show.Slug);
        Assert.Equal($"tm-{s.Suffix}", show.ExternalIds[s.Primary.Key]); // its own id, not SeatGeek's claim
        Assert.Equal($"sg-{s.Suffix}", show.ExternalIds[s.Resale.Key]);
        Assert.Equal(Start, show.StartsAt);
        Assert.Equal(Start.AddDays(-30), show.OnSaleAt);

        // What pointed at the duplicate points at the twin.
        var watch = await db.Watches.SingleAsync(w => w.EventId == s.Twin.Id);
        Assert.Equal(45m, watch.TargetPrice);
        Assert.Equal(1, await db.OnSaleTicks.CountAsync(t => t.EventId == s.Twin.Id));
        Assert.Equal(1, await db.PriceObservations.CountAsync(o => o.EventId == s.Twin.Id));

        // The show only SeatGeek had moves rooms and keeps its address.
        var lone = await db.Events.SingleAsync(e => e.Id == s.Lone.Id);
        Assert.Equal(s.Kept.Id, lone.VenueId);
        Assert.Equal($"only-seatgeek-{s.Suffix}", lone.Slug);

        // Second start: nothing to do for this room.
        var second = await Initializer(db).MergeDuplicateVenuesAsync();
        Assert.DoesNotContain(second, c => c.Pair.KeptId == s.Kept.Id);
        Assert.Equal(2, await db.Events.CountAsync(e => e.VenueId == s.Kept.Id));
    }

    [Fact]
    public async Task A_subscriber_s_link_survives_and_one_address_stays_one_subscription()
    {
        await using var db = fixture.CreateContext();
        var s = await SetUpAsync(db);

        // Both events have a watch: one survives, and it is on because either was.
        db.Watches.AddRange(
            new Watch { EventId = s.Twin.Id, Enabled = false, CreatedAt = Start.AddDays(-40) },
            new Watch { EventId = s.Duplicate.Id, Enabled = true, TargetPrice = 60m, CreatedAt = Start.AddDays(-40) });

        // Only the duplicate's address was ever mailed: the twin takes it.
        var email = $"fan-{s.Suffix}@example.com";
        db.Subscriptions.Add(new Subscription
        {
            Email = email,
            EventId = s.Duplicate.Id,
            CreatedAt = Start.AddDays(-20),
            ConfirmedAt = Start.AddDays(-20),
            ConfirmToken = $"c-{s.Suffix}",
            UnsubscribeToken = $"u-{s.Suffix}"
        });
        await db.SaveChangesAsync();

        var counts = Assert.Single(await Initializer(db).MergeDuplicateVenuesAsync(), c => c.Pair.KeptId == s.Kept.Id);
        Assert.Equal(1, counts.SlugsAdopted);
        Assert.Equal(1, counts.Subscriptions);

        db.ChangeTracker.Clear();

        var show = await db.Events.SingleAsync(e => e.Id == s.Twin.Id);
        Assert.Equal($"acid-bath-the-truth-nashville-{s.Suffix}", show.Slug);

        var watch = await db.Watches.SingleAsync(w => w.EventId == s.Twin.Id);
        Assert.True(watch.Enabled);
        Assert.Equal(60m, watch.TargetPrice);

        var subscription = await db.Subscriptions.SingleAsync(x => x.Email == email);
        Assert.Equal(s.Twin.Id, subscription.EventId);
        Assert.NotNull(subscription.ConfirmedAt);
    }

    [Fact]
    public async Task Twins_inside_one_room_are_folded_and_one_source_s_two_listings_are_not()
    {
        await using var db = fixture.CreateContext();
        var s = await SetUpAsync(db);

        // A show SeatGeek named first, before Ticketmaster: same room, same night, its own
        // row, because SeatGeek's legacy Ticketmaster id failed Ticketmaster's slow path.
        var night = Start.AddDays(14);
        var sting = new Performer { Name = $"Sting {s.Suffix}" };
        db.Performers.Add(sting);
        await db.SaveChangesAsync();

        var tm = new Event
        {
            Name = "STING 3.0 Tour",
            Slug = $"sting-{s.Suffix}",
            VenueId = s.Kept.Id,
            PerformerId = sting.Id,
            StartsAt = night,
            CreatedBySource = s.Primary.Key,
            ExternalIds = new() { [s.Primary.Key] = $"tm-sting-{s.Suffix}" }
        };
        db.Events.Add(tm);
        await db.SaveChangesAsync();

        var sg = new Event
        {
            Name = "Sting",
            Slug = $"sting-2-{s.Suffix}",
            VenueId = s.Kept.Id,
            PerformerId = sting.Id,
            StartsAt = night,
            CreatedBySource = s.Resale.Key,
            ExternalIds = new() { [s.Resale.Key] = $"sg-sting-{s.Suffix}", [s.Primary.Key] = $"1B00sting{s.Suffix}" }
        };

        // And two listings the one source told apart: an early and a late show.
        var early = new Event
        {
            Name = $"Matinee {s.Suffix}",
            Slug = $"matinee-early-{s.Suffix}",
            VenueId = s.Kept.Id,
            StartsAt = night.AddDays(1),
            CreatedBySource = s.Primary.Key,
            ExternalIds = new() { [s.Primary.Key] = $"tm-early-{s.Suffix}" }
        };
        var late = new Event
        {
            Name = $"Matinee {s.Suffix}",
            Slug = $"matinee-late-{s.Suffix}",
            VenueId = s.Kept.Id,
            StartsAt = night.AddDays(1).AddHours(3),
            CreatedBySource = s.Primary.Key,
            ExternalIds = new() { [s.Primary.Key] = $"tm-late-{s.Suffix}" }
        };
        db.Events.AddRange(sg, early, late);
        await db.SaveChangesAsync();

        db.OnSaleTicks.Add(new OnSaleTick
        {
            EventId = sg.Id,
            SourceId = s.Resale.Id,
            ObservedAt = night.AddDays(-20),
            MinutesFromOnSale = 5,
            IngestionRunId = s.RunId
        });
        await db.SaveChangesAsync();

        var first = await Initializer(db).MergeTwinEventsAsync();
        var counts = Assert.Single(first, c => c.Pair.KeptId == s.Kept.Id);
        Assert.Equal(1, counts.EventsMerged);
        Assert.Equal(1, counts.Ticks);

        db.ChangeTracker.Clear();

        Assert.False(await db.Events.AnyAsync(e => e.Id == sg.Id));
        var show = await db.Events.SingleAsync(e => e.Id == tm.Id);
        Assert.Equal($"sting-{s.Suffix}", show.Slug);
        Assert.Equal($"tm-sting-{s.Suffix}", show.ExternalIds[s.Primary.Key]);
        Assert.Equal($"sg-sting-{s.Suffix}", show.ExternalIds[s.Resale.Key]);
        Assert.Equal(1, await db.OnSaleTicks.CountAsync(t => t.EventId == tm.Id));

        Assert.True(await db.Events.AnyAsync(e => e.Id == early.Id));
        Assert.True(await db.Events.AnyAsync(e => e.Id == late.Id));

        var second = await Initializer(db).MergeTwinEventsAsync();
        Assert.DoesNotContain(second, c => c.Pair.KeptId == s.Kept.Id);
    }
}
