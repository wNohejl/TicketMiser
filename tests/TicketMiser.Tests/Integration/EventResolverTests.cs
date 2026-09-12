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
