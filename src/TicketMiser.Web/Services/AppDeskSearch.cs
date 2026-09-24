using MudBlazor;
using TicketMiser.Desk.Windowing;
using TicketMiser.Web.Components.Prices;
using TicketMiser.Web.Windowing;

namespace TicketMiser.Web.Services;

/// <summary>
/// What TicketMiser adds to the command palette: performers, events and venues, each opening its
/// window. The desk supplies windows and workspaces itself.
/// </summary>
public sealed class AppDeskSearch(ISearchQueries search, WindowManager manager) : IDeskSearch
{
    public async Task<IReadOnlyList<DeskSearchResult>> SearchAsync(string text, CancellationToken ct)
    {
        // Each query takes its own context from the factory, so two quick searches never share one.
        var performers = await search.PerformersAsync(text, 5, ct);
        var events = await search.EventsAsync(text, 6, ct);
        var venues = await search.VenuesAsync(text, 5, ct);

        return
        [
            .. performers.Select(p => new DeskSearchResult(
                "Performers", p.Name, null,
                Icons.Material.Filled.Person, () => Destinations.OpenPerformer(manager, p.Id, p.Name))),

            .. events.Select(e => new DeskSearchResult(
                "Events", e.Name,
                string.Join(" · ", new[] { PriceText.When(e.StartsAt, e.Venue?.Timezone), e.Venue?.Name, e.Status == Core.Entities.EventStatus.Cancelled ? "cancelled" : null }
                    .Where(x => !string.IsNullOrEmpty(x))),
                Icons.Material.Filled.Event, () => Destinations.OpenEvent(manager, e))),

            .. venues.Select(v => new DeskSearchResult(
                "Venues", v.Name, string.Join(", ", new[] { v.City, v.State }.Where(x => !string.IsNullOrEmpty(x))),
                Icons.Material.Filled.Place, () => Destinations.OpenVenue(manager, v.Id, v.Name)))
        ];
    }
}
