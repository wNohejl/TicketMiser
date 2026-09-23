using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// SeatGeek names a show by Ticketmaster's legacy host id, and until the adapter learned that,
/// the id sat under the <c>ticketmaster</c> key: it was fetched as if it were a Discovery id, and
/// it kept Ticketmaster's own listing of the same show from joining it. The startup repair files
/// it under its own key, after which the two listings are one event.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LegacyTicketmasterIdTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2027, 4, 10, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_misfiled_legacy_id_is_refiled_and_the_two_listings_become_one_event()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var legacy = $"1B00{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12]}";

        var venue = new Venue { Name = $"Legacy Room {suffix}", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        var performer = new Performer { Name = $"Legacy Band {suffix}" };
        db.AddRange(venue, performer);
        await db.SaveChangesAsync();

        var fromTicketmaster = new Event
        {
            Name = $"Legacy Band {suffix}",
            VenueId = venue.Id,
            PerformerId = performer.Id,
            StartsAt = Start,
            CreatedBySource = "ticketmaster",
            ExternalIds = new() { ["ticketmaster"] = $"G5v{suffix}", [ExternalIdKeys.TicketmasterLegacy] = legacy }
        };
        var fromSeatGeek = new Event
        {
            Name = $"LEGACY BAND {suffix}",
            VenueId = venue.Id,
            PerformerId = performer.Id,
            StartsAt = Start.AddMinutes(30),
            CreatedBySource = "seatgeek",
            ExternalIds = new() { ["seatgeek"] = $"sg-{suffix}", ["ticketmaster"] = legacy }
        };
        db.Events.AddRange(fromTicketmaster, fromSeatGeek);
        await db.SaveChangesAsync();

        var initializer = new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance);

        Assert.True(await initializer.RefileLegacyTicketmasterIdsAsync() >= 1);
        await initializer.MergeTwinEventsAsync();

        db.ChangeTracker.Clear();
        var left = await db.Events.Where(e => e.VenueId == venue.Id).ToListAsync();

        var one = Assert.Single(left);
        Assert.Equal($"G5v{suffix}", one.ExternalIds["ticketmaster"]);
        Assert.Equal($"sg-{suffix}", one.ExternalIds["seatgeek"]);
        Assert.Equal(legacy, one.ExternalIds[ExternalIdKeys.TicketmasterLegacy]);

        // A second start finds nothing to move.
        Assert.Equal(0, await initializer.RefileLegacyTicketmasterIdsAsync());
    }

    [Fact]
    public async Task An_id_ticketmaster_gave_for_its_own_event_is_never_moved()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var venue = new Venue { Name = $"Own Id Room {suffix}", City = "Nashville", State = "TN" };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();

        var hexShaped = $"2C00{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12]}";
        var evt = new Event
        {
            Name = $"Own Id {suffix}",
            VenueId = venue.Id,
            StartsAt = Start,
            CreatedBySource = "ticketmaster",
            ExternalIds = new() { ["ticketmaster"] = hexShaped }
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).RefileLegacyTicketmasterIdsAsync();

        db.ChangeTracker.Clear();
        var reread = await db.Events.SingleAsync(e => e.Id == evt.Id);
        Assert.Equal(hexShaped, reread.ExternalIds["ticketmaster"]);
    }
}
