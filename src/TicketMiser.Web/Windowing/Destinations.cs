using TicketMiser.Core.Entities;
using TicketMiser.Desk.Windowing;

namespace TicketMiser.Web.Windowing;

/// <summary>
/// Following a name. Every place an event, performer or venue is named and can be followed goes
/// through here, so each destination is opened with the same parameter name and title wherever
/// it is reached from. None is a singleton: two venues side by side is a comparison.
/// </summary>
public static class Destinations
{
    public const string EventId = "EventId";
    public const string PerformerId = "PerformerId";
    public const string VenueId = "VenueId";

    public static void OpenEvent(WindowManager manager, Event evt)
        => Open(manager, WindowCatalog.Event, EventId, evt.Id, evt.Name);

    public static void OpenPriceHistory(WindowManager manager, Event evt)
        => Open(manager, WindowCatalog.PriceHistory, EventId, evt.Id, evt.Name);

    public static void OpenPerformer(WindowManager manager, int performerId, string name)
        => Open(manager, WindowCatalog.Performer, PerformerId, performerId, name);

    public static void OpenVenue(WindowManager manager, int venueId, string name)
        => Open(manager, WindowCatalog.Venue, VenueId, venueId, name);

    /// <summary>The performer's id as the event carries it, or null when there is nothing to follow.</summary>
    public static int? PerformerOf(Event evt)
        => evt.PerformerId is > 0 ? evt.PerformerId : evt.Performer is { Id: > 0 } p ? p.Id : null;

    /// <summary>The venue's id as the event carries it, or null when there is nothing to follow.</summary>
    public static int? VenueOf(Event evt)
        => evt.VenueId > 0 ? evt.VenueId : evt.Venue is { Id: > 0 } v ? v.Id : null;

    /// <summary>A window parameter as an id, whichever shape it arrived in — an int from a click, a long or string from a restored desk.</summary>
    public static int? IdFrom(IReadOnlyDictionary<string, object>? parameters, string name)
        => parameters?.GetValueOrDefault(name) switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    private static void Open(WindowManager manager, string key, string parameter, int id, string title)
    {
        if (WindowCatalog.Find(key) is { } definition)
            manager.Open(definition, new() { [parameter] = id }, title);
    }
}
