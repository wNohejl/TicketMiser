using System.Text.Json;

namespace TicketMiser.Core.Entities;

/// <summary>
/// One presale window as stored on <see cref="Event.Presales"/>: the resolver serialises the
/// source's list as <c>[{ name, startsAt, endsAt }]</c>, and everything that reads it back
/// reads it through here so the shape is written down once.
/// </summary>
public sealed record EventPresale(string? Name, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The windows on an event, or none when the column is empty or unreadable.</summary>
    public static IReadOnlyList<EventPresale> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<EventPresale>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
