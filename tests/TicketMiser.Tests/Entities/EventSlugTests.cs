using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Entities;

/// <summary>
/// The address of a record: performer, room and night, folded to ASCII, and never the same
/// for two events. Deterministic, so two machines mint the same link for the same event.
/// </summary>
public class EventSlugTests
{
    private static readonly DateTimeOffset Night = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_base_is_performer_venue_and_the_night_in_the_venue_zone()
    {
        // 01:00 UTC on 20 November is the evening of the 19th in Nashville.
        var slug = EventSlug.Base("Example Performer", "Example Tour", "Bridgestone Arena", Night, "America/Chicago");

        Assert.Equal("example-performer-bridgestone-arena-2026-11-19", slug);
    }

    [Fact]
    public void Without_a_performer_or_a_zone_the_name_and_the_utc_date_stand_in()
    {
        Assert.Equal("example-tour-ryman-auditorium-2026-11-20",
            EventSlug.Base(null, "Example Tour", "Ryman Auditorium", Night, null));

        Assert.Equal("example-tour-ryman-auditorium-2026-11-20",
            EventSlug.Base("", "Example Tour", "Ryman Auditorium", Night, "Not/AZone"));
    }

    [Fact]
    public void The_slug_is_read_off_the_row_the_same_way()
    {
        var evt = new Event
        {
            Name = "Example Tour",
            StartsAt = Night,
            Performer = new Performer { Name = "Example Performer" },
            Venue = new Venue { Name = "Bridgestone Arena", Timezone = "America/Chicago" }
        };

        Assert.Equal("example-performer-bridgestone-arena-2026-11-19", EventSlug.Base(evt));
    }

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Sigur Rós", "sigur-ros")]
    [InlineData("3rd & Lindsley", "3rd-and-lindsley")]
    [InlineData("Exit/In", "exit-in")]
    [InlineData("  The  Basement East ", "the-basement-east")]
    [InlineData("AC/DC — Power Up!", "ac-dc-power-up")]
    [InlineData("Мумий Тролль", "")]
    [InlineData("Ólafur Arnalds & Nils Frahm", "olafur-arnalds-and-nils-frahm")]
    public void Folding_is_lower_cased_ascii_with_one_hyphen_between_words(string text, string folded)
    {
        Assert.Equal(folded, EventSlug.Fold(text));
    }

    [Fact]
    public void An_event_with_nothing_foldable_is_still_addressed()
    {
        Assert.Equal("2026-11-20", EventSlug.Base("Мумий Тролль", "Мумий Тролль", "", Night, null));
        Assert.Equal("event", EventSlug.Base("", "", "", DateTimeOffset.MinValue, null).Length > 0 ? "event" : "");
    }

    [Fact]
    public void A_long_name_is_cut_on_a_word_inside_the_column()
    {
        var name = string.Join(' ', Enumerable.Repeat("word", 60));

        var slug = EventSlug.Base(null, name, "Bridgestone Arena", Night, null);

        Assert.True(slug.Length <= EventSlug.MaxLength - 4);
        Assert.StartsWith("word-word", slug);
        // The bill is what gets cut; the room and the night are what make the address an address.
        Assert.EndsWith("-word-bridgestone-arena-2026-11-20", slug);
    }

    [Fact]
    public void A_room_whose_name_fills_the_column_still_fits_it()
    {
        var venue = string.Join(' ', Enumerable.Repeat("room", 60));

        var slug = EventSlug.Base("Band", "Band", venue, Night, null);

        Assert.True(slug.Length <= EventSlug.MaxLength - 4);
        Assert.False(slug.EndsWith('-'));
        Assert.StartsWith("band-room", slug);
    }

    [Fact]
    public void A_taken_slug_takes_the_first_free_numeric_suffix()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "band-room-2026-11-19", "band-room-2026-11-19-2" };

        Assert.Equal("band-room-2026-11-19-3", EventSlug.Unique("band-room-2026-11-19", taken));
        Assert.Equal("band-room-2026-11-20", EventSlug.Unique("band-room-2026-11-20", taken));
    }
}
