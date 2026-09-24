using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Web.Services;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// What "bryan" finds. One rule for the palette, the Performers window and the watch picker:
/// every word matches one of the fields it could mean, events in a disabled category stay
/// hidden, and a typed wildcard is a character.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SearchQueriesTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<TicketMiserDbContext>
    {
        public TicketMiserDbContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed record World(Venue Room, Venue Hall, Performer Headliner, Performer Quiet, Event Next, Event Last, Event Hidden, string Tag);

    private SearchQueries Search() => new(new Factory(fixture), new FakeClock(Now));

    private async Task<World> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..6];

        var hidden = new Category { Key = $"hidden-{tag}", Name = "Hidden", Enabled = false };
        var room = new Venue { Name = $"Riverside {tag} Room", City = $"Harbor{tag}", State = "TN", Timezone = "America/Chicago" };
        var hall = new Venue { Name = $"Summit {tag} Hall", City = "Nashville", State = "TN", Timezone = "America/Chicago" };
        var headliner = new Performer { Name = $"Juniper {tag} Vale" };
        var quiet = new Performer { Name = $"Juniper {tag} Moss" };
        db.AddRange(hidden, room, hall, headliner, quiet);
        await db.SaveChangesAsync();

        var next = new Event { Name = $"Vale {tag} Tour", PerformerId = headliner.Id, VenueId = room.Id, StartsAt = Now.AddDays(10) };
        var last = new Event { Name = $"Vale {tag} Tour", PerformerId = headliner.Id, VenueId = hall.Id, StartsAt = Now.AddDays(-3) };
        var hiddenEvent = new Event { Name = $"Vale {tag} Tour", PerformerId = headliner.Id, VenueId = hall.Id, StartsAt = Now.AddDays(5), CategoryId = hidden.Id };
        db.Events.AddRange(next, last, hiddenEvent);
        await db.SaveChangesAsync();

        return new World(room, hall, headliner, quiet, next, last, hiddenEvent, tag);
    }

    [Fact]
    public async Task Every_word_must_match_one_of_the_fields_it_could_mean()
    {
        var w = await SeedAsync();

        // Performer and venue in one query: only the show at that room.
        var found = await Search().EventsAsync($"juniper {w.Tag} riverside");
        Assert.Equal([w.Next.Id], found.Select(e => e.Id));

        // A city is a field an event can be found by.
        Assert.Contains(await Search().EventsAsync($"harbor{w.Tag}"), e => e.Id == w.Next.Id);

        // A word nothing matches rules the rest out.
        Assert.Empty(await Search().EventsAsync($"juniper {w.Tag} nowhere"));
    }

    [Fact]
    public async Task Upcoming_events_lead_and_a_disabled_category_stays_hidden()
    {
        var w = await SeedAsync();

        var found = await Search().EventsAsync($"vale {w.Tag}");

        Assert.Equal([w.Next.Id, w.Last.Id], found.Select(e => e.Id));
        Assert.DoesNotContain(found, e => e.Id == w.Hidden.Id);
    }

    [Fact]
    public async Task A_performer_with_a_show_coming_leads_one_without()
    {
        var w = await SeedAsync();

        var found = await Search().PerformersAsync($"juniper {w.Tag}");

        Assert.Equal([w.Headliner.Id, w.Quiet.Id], found.Select(p => p.Id));
    }

    [Fact]
    public async Task A_venue_is_found_by_name_or_city()
    {
        var w = await SeedAsync();

        Assert.Equal([w.Room.Id], (await Search().VenuesAsync($"riverside {w.Tag}")).Select(v => v.Id));
        Assert.Contains(await Search().VenuesAsync($"summit {w.Tag} nashville"), v => v.Id == w.Hall.Id);
    }

    [Fact]
    public async Task A_typed_wildcard_is_a_character_to_find()
    {
        var w = await SeedAsync();

        // Unescaped, "%" would match every performer and "_" any one character.
        Assert.Empty(await Search().PerformersAsync($"juniper {w.Tag} %"));
        Assert.Empty(await Search().PerformersAsync($"juniper_{w.Tag}"));
    }

    [Fact]
    public async Task Nothing_typed_finds_nothing()
    {
        Assert.Empty(await Search().EventsAsync("   "));
        Assert.Empty(await Search().PerformersAsync(""));
        Assert.Empty(await Search().VenuesAsync(" "));
    }

    [Theory]
    [InlineData("juniper vale", true)]
    [InlineData("VALE", true)]
    [InlineData("juniper nashville", true)]
    [InlineData("juniper memphis", false)]
    [InlineData("", true)]
    public void The_rule_in_memory_is_the_same_rule(string text, bool matches)
        => Assert.Equal(matches, SearchQueries.MatchesAllWords(text, "Juniper Vale", null, "Nashville"));
}
