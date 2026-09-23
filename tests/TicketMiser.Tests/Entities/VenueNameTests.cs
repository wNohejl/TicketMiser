using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Ingestion.Services;

namespace TicketMiser.Tests.Entities;

/// <summary>
/// One room, two sources' names for it. SeatGeek appends the town ("The Truth - Nashville"),
/// Ticketmaster sometimes the state ("The Pinnacle - TN"); the key sets aside only the room's
/// own locality, so a name that merely contains the town is left alone.
/// </summary>
public class VenueNameTests
{
    [Theory]
    [InlineData("The Truth")]
    [InlineData("The Truth - Nashville")]
    [InlineData("The Truth – Nashville")]
    [InlineData("The Truth, Nashville")]
    [InlineData("The Truth (Nashville)")]
    [InlineData("The Truth - Nashville, TN")]
    [InlineData("The Truth (Nashville, TN)")]
    [InlineData("THE TRUTH - NASHVILLE")]
    [InlineData("The Truth - TN")]
    [InlineData("  The Truth - Nashville  ")]
    public void A_trailing_locality_of_the_room_s_own_town_is_set_aside(string name)
        => Assert.Equal("thetruth", VenueName.Key(name, "Nashville", "TN"));

    [Theory]
    [InlineData("Nashville Municipal Auditorium", "nashvillemunicipalauditorium")]
    [InlineData("Brooklyn Bowl Nashville", "brooklynbowlnashville")]
    [InlineData("Ole Red Nashville", "olerednashville")]
    [InlineData("Nashville", "nashville")]
    [InlineData("Cannery Hall - Row One Stage", "canneryhallrowonestage")]
    [InlineData("The Basement-TN Parking", "thebasementtnparking")]
    [InlineData("Foo-Nashville", "foonashville")]
    public void A_name_that_merely_contains_the_town_is_untouched(string name, string expected)
        => Assert.Equal(expected, VenueName.Key(name, "Nashville", "TN"));

    [Theory]
    [InlineData("City Winery - Memphis", "citywinerymemphis")]
    [InlineData("The Pinnacle - GA", "thepinnaclega")]
    [InlineData("The Truth (Franklin)", "thetruthfranklin")]
    public void Another_place_s_suffix_is_not_stripped(string name, string expected)
        => Assert.Equal(expected, VenueName.Key(name, "Nashville", "TN"));

    [Fact]
    public void Without_a_town_or_state_the_key_is_the_plain_fold()
    {
        Assert.Equal("thetruthnashville", VenueName.Key("The Truth - Nashville", null, null));
        Assert.Equal(VenueName.Normalise("3rd & Lindsley"), VenueName.Key("3rd & Lindsley", null, null));
    }

    [Fact]
    public void The_repair_uses_the_resolver_s_drift_window()
        => Assert.Equal(EventResolver.SameFixtureDrift, DatabaseInitializer.SameFixtureDrift);

    [Fact]
    public void Duplicate_rooms_pair_to_the_seeded_or_oldest_row_and_never_delete_a_seeded_one()
    {
        static Venue V(int id, string name, string city = "Nashville")
            => new() { Id = id, Name = name, City = city, State = "TN" };

        var pairs = DatabaseInitializer.FindDuplicateVenues(
        [
            V(5, "Brooklyn Bowl Nashville"),
            V(19, "The Pinnacle - TN"),
            V(24, "The Truth"),
            V(26, "Nashville Municipal Auditorium"),
            V(36, "The Pinnacle - Nashville"),
            V(40, "The Truth - Nashville"),
            V(41, "Brooklyn Bowl - Nashville"),
            V(60, "The Truth", city: "Memphis"),
            V(61, "City Winery - Nashville")
        ]);

        Assert.Equal(
            [new(5, 41), new(19, 36), new(24, 40)],
            pairs.OrderBy(p => p.KeptId).ToList());
    }

    [Fact]
    public void A_source_s_claim_about_another_s_id_does_not_keep_two_rows_apart_but_its_own_ids_do()
    {
        var tm = new Event { Id = 1, CreatedBySource = "ticketmaster", ExternalIds = new() { ["ticketmaster"] = "G5viZ" } };
        var sg = new Event
        {
            Id = 2,
            CreatedBySource = "seatgeek",
            ExternalIds = new() { ["seatgeek"] = "18", ["ticketmaster"] = "1B00650D" }
        };
        var otherShow = new Event { Id = 3, CreatedBySource = "ticketmaster", ExternalIds = new() { ["ticketmaster"] = "G5late" } };

        Assert.True(DatabaseInitializer.MayBeOneFixture(sg, tm));
        Assert.True(DatabaseInitializer.MayBeOneFixture(tm, sg));
        Assert.False(DatabaseInitializer.MayBeOneFixture(otherShow, tm));

        // Each source's own id survives the union; the claim is dropped.
        var ids = DatabaseInitializer.UnionIds(sg, tm);
        Assert.Equal("G5viZ", ids["ticketmaster"]);
        Assert.Equal("18", ids["seatgeek"]);

        ids = DatabaseInitializer.UnionIds(tm, sg);
        Assert.Equal("G5viZ", ids["ticketmaster"]);
    }
}
