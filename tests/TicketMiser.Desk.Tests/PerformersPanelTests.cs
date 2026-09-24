using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Panels;
using TicketMiser.Web.Services;
using TicketMiser.Web.Windowing;
using static TicketMiser.Desk.Tests.PriceFixtures;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The Performers window lists and counts, and a name is the way to the Performer window about
/// that performer.
/// </summary>
public class PerformersPanelTests : DeskTestContext
{
    private readonly FakeDestinationQueries _fake = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private WindowManager _manager = default!;

    private IRenderedComponent<PerformersPanel> Open()
    {
        _manager = new WindowManager(new AppWindowCatalog());
        Services.AddSingleton(_manager);
        Services.AddSingleton<IDestinationQueries>(_fake);
        Services.AddSingleton<TimeProvider>(_clock);

        return Render<PerformersPanel>();
    }

    [Fact]
    public void With_no_performer_on_record_it_says_where_performers_come_from()
    {
        var cut = Open();

        Assert.Contains("daily discovery run", cut.Find(".empty--new").TextContent);
    }

    [Fact]
    public void Each_performer_carries_its_upcoming_count()
    {
        _fake.Performers.Add(new PerformerSummary(Performer(), 3, Now.AddDays(12), "America/Chicago"));
        _fake.Performers.Add(new PerformerSummary(new Core.Entities.Performer { Id = 21, Name = "Quiet Act" }, 0, null));

        var cut = Open();

        var rows = cut.FindAll("tbody .mud-table-row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("3 events", System.Text.RegularExpressions.Regex.Replace(rows[0].TextContent, @"\s+", " "));
        Assert.Contains("none ahead", rows[1].TextContent);
    }

    [Fact]
    public void A_search_that_matches_nobody_says_so()
    {
        _fake.Performers.Add(new PerformerSummary(Performer(), 3, Now.AddDays(12)));

        var cut = Open();
        cut.Find("input").Input("nobody");

        // The query waits for the typing to pause, and the pause is the test's to give.
        Assert.DoesNotContain("nobody", _fake.Searches);
        _clock.Advance(TimeSpan.FromMilliseconds(250));

        cut.WaitForAssertion(() => Assert.Contains("No performer matches", cut.Find(".empty--filtered").TextContent));
        Assert.Contains("nobody", _fake.Searches);
    }

    [Fact]
    public void Following_a_name_opens_the_performer_window_about_that_performer()
    {
        _fake.Performers.Add(new PerformerSummary(Performer(), 3, Now.AddDays(12)));

        var cut = Open();
        cut.Find("button.desklink").Click();

        var window = Assert.Single(_manager.Windows, w => w.Definition.Key == WindowCatalog.Performer);
        Assert.Equal(20, window.Parameters["PerformerId"]);
        Assert.Equal("Example Performer", window.Title);
    }
}
