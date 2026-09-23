using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Services;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// The two lessons LineOps paid for, plus the one Nashville adds: the drift window is one
/// fixture's, not a day; only the schedule authority moves a start; venue is never optional.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EventResolverTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

    private static async Task SeedSourcesAsync(TicketMiserDbContext db, string suffix)
    {
        db.Sources.AddRange(
            new Source { Key = $"primary-{suffix}", Name = "Primary", Kind = SourceKind.Primary },
            new Source { Key = $"resale-{suffix}", Name = "Resale", Kind = SourceKind.Resale });
        await db.SaveChangesAsync();
    }

    private static CanonicalEvent At(string id, string venue, string performer, DateTimeOffset startsAt, DateTimeOffset? onSale = null)
        => new(id, performer,
            new CanonicalVenueRef(venue, null, "Nashville", "TN", "US", "America/Chicago"),
            startsAt, new CanonicalPerformerRef(performer), "concert", "onsale", onSale);

    [Fact]
    public async Task Two_sources_naming_the_same_night_at_the_same_venue_resolve_to_one_event()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        var primary = await resolver.ResolveEventAsync($"primary-{suffix}",
            At("tm-1", $"Venue {suffix}", $"Band {suffix}", Start, Start.AddDays(-30)), default);

        // The resale site is three minutes off and spells nothing differently.
        var resale = await resolver.ResolveEventAsync($"resale-{suffix}",
            At("sg-1", $"Venue {suffix}", $"Band {suffix}", Start.AddMinutes(3)), default);

        Assert.Equal(primary.Id, resale.Id);
        Assert.Equal("tm-1", resale.ExternalIds[$"primary-{suffix}"]);
        Assert.Equal("sg-1", resale.ExternalIds[$"resale-{suffix}"]);
        Assert.Equal($"primary-{suffix}", resale.CreatedBySource);
    }

    [Fact]
    public async Task SeatGeek_s_town_suffix_on_a_room_name_resolves_to_the_same_room_and_the_same_show()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        // Ticketmaster: "The Truth"; SeatGeek: "The Truth - Nashville", its own venue id, and
        // the bill in different case. The observed Acid Bath pair, one room and one night.
        var primary = await resolver.ResolveEventAsync($"primary-{suffix}", new CanonicalEvent(
            "tm-ab", "ACID BATH with JEFF THE BROTHERHOOD",
            new CanonicalVenueRef($"The Truth {suffix}", $"tm-v-{suffix}", "Nashville", "TN", "US", "America/Chicago"),
            Start, new CanonicalPerformerRef($"ACID BATH {suffix}", $"tm-p-{suffix}"), "concert", "onsale"), default);

        var resale = await resolver.ResolveEventAsync($"resale-{suffix}", new CanonicalEvent(
            "sg-ab", "Acid Bath with JEFF the Brotherhood",
            new CanonicalVenueRef($"The Truth {suffix} - Nashville", $"sg-v-{suffix}", "Nashville", "TN", "US", "America/Chicago"),
            Start.AddMinutes(30), new CanonicalPerformerRef($"Acid Bath {suffix}", $"sg-p-{suffix}"), "concert"), default);

        Assert.Equal(primary.Id, resale.Id);
        Assert.Equal(primary.VenueId, resale.VenueId);

        var venues = await db.Venues.Where(v => v.Name.Contains(suffix)).ToListAsync();
        var venue = Assert.Single(venues);
        Assert.Equal($"The Truth {suffix}", venue.Name);
        Assert.Equal($"tm-v-{suffix}", venue.ExternalIds[$"primary-{suffix}"]);
        Assert.Equal($"sg-v-{suffix}", venue.ExternalIds[$"resale-{suffix}"]);
    }

    [Fact]
    public async Task Another_town_s_suffix_is_another_room()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        var nashville = await resolver.ResolveVenueAsync($"primary-{suffix}",
            new CanonicalVenueRef($"City Winery {suffix}", null, "Nashville", "TN"), default);
        var memphis = await resolver.ResolveVenueAsync($"resale-{suffix}",
            new CanonicalVenueRef($"City Winery {suffix} - Memphis", null, "Memphis", "TN"), default);

        Assert.NotEqual(nashville.Id, memphis.Id);
    }

    [Fact]
    public async Task The_same_performer_at_a_different_venue_the_same_night_is_a_different_event()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        var ryman = await resolver.ResolveEventAsync($"primary-{suffix}",
            At("tm-r", $"Ryman {suffix}", $"Artist {suffix}", Start), default);
        var opry = await resolver.ResolveEventAsync($"resale-{suffix}",
            At("sg-o", $"Opry {suffix}", $"Artist {suffix}", Start), default);

        Assert.NotEqual(ryman.Id, opry.Id);
    }

    [Fact]
    public async Task A_second_night_of_a_residency_is_outside_the_drift_window()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        var night1 = await resolver.ResolveEventAsync($"primary-{suffix}",
            At("tm-n1", $"Venue {suffix}", $"Res {suffix}", Start), default);
        var night2 = await resolver.ResolveEventAsync($"resale-{suffix}",
            At("sg-n2", $"Venue {suffix}", $"Res {suffix}", Start.AddHours(24)), default);

        Assert.NotEqual(night1.Id, night2.Id);
    }

    [Fact]
    public async Task Only_the_authority_moves_a_start_or_an_on_sale_time_and_anyone_fills_a_gap()
    {
        await using var db = fixture.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedSourcesAsync(db, suffix);
        var resolver = new EventResolver(db);

        var evt = await resolver.ResolveEventAsync($"primary-{suffix}",
            At("tm-m", $"Venue {suffix}", $"Mover {suffix}", Start), default);
        Assert.Null(evt.OnSaleAt);

        // The resale site names the fixture and happens to carry an on-sale time: it fills the gap.
        var viaResale = await resolver.ResolveEventAsync($"resale-{suffix}",
            At("sg-m", $"Venue {suffix}", $"Mover {suffix}", Start.AddMinutes(2), Start.AddDays(-20)), default);
        Assert.Equal(Start.AddDays(-20), viaResale.OnSaleAt);
        Assert.Equal(Start, viaResale.StartsAt);

        // The resale site now says something different: ignored on both counts.
        viaResale = await resolver.ResolveEventAsync($"resale-{suffix}",
            At("sg-m", $"Venue {suffix}", $"Mover {suffix}", Start.AddHours(1), Start.AddDays(-19)), default);
        Assert.Equal(Start.AddDays(-20), viaResale.OnSaleAt);
        Assert.Equal(Start, viaResale.StartsAt);

        // The primary market moves both, because it is the authority on the fixture it named.
        var moved = await resolver.ResolveEventAsync($"primary-{suffix}",
            At("tm-m", $"Venue {suffix}", $"Mover {suffix}", Start.AddHours(1), Start.AddDays(-18)), default);
        Assert.Equal(Start.AddHours(1), moved.StartsAt);
        Assert.Equal(Start.AddDays(-18), moved.OnSaleAt);

        Assert.Equal(1, await db.Events.CountAsync(e => e.Id == moved.Id));
    }
}
