using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Web.Services;

/// <summary>
/// Finds events, performers and venues by what a person would type. One rule for it, so the
/// command palette, the Performers window and the watch picker agree on what "bryan" matches.
///
/// <para>
/// Every word must match, in any of the fields a word could mean — "bryan bridgestone" is that
/// performer at that room, "ryman nashville" the venue in that city. Typed <c>%</c> and <c>_</c>
/// are characters to find, not wildcards. Events in a disabled category are left out: a kind of
/// event the desk hides from its pickers should not come back through the search.
/// </para>
/// </summary>
public interface ISearchQueries
{
    Task<IReadOnlyList<Event>> EventsAsync(string text, int take = 6, CancellationToken ct = default);

    Task<IReadOnlyList<Performer>> PerformersAsync(string text, int take = 6, CancellationToken ct = default);

    Task<IReadOnlyList<Venue>> VenuesAsync(string text, int take = 6, CancellationToken ct = default);
}

public sealed class SearchQueries(IDbContextFactory<TicketMiserDbContext> factory, TimeProvider clock) : ISearchQueries
{
    /// <summary>
    /// Events whose name, performer, venue or city matches every word, a year either side of
    /// now. Upcoming ones lead, soonest first, then past ones, latest first: the show being looked
    /// for is usually the next one, and the one being logged late usually last week's.
    /// </summary>
    public async Task<IReadOnlyList<Event>> EventsAsync(string text, int take = 6, CancellationToken ct = default)
    {
        var patterns = Patterns(text).ToList();
        if (patterns.Count == 0)
            return [];

        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();

        var query = db.Events.AsNoTracking()
            .Include(e => e.Performer).Include(e => e.Venue)
            .Where(e => (e.Category == null || e.Category.Enabled)
                        && e.StartsAt >= now.AddDays(-365)
                        && e.StartsAt <= now.AddDays(365));

        foreach (var pattern in patterns)
        {
            query = query.Where(e =>
                EF.Functions.ILike(e.Name, pattern, Escape)
                || (e.Performer != null && EF.Functions.ILike(e.Performer.Name, pattern, Escape))
                || (e.Venue != null && (EF.Functions.ILike(e.Venue.Name, pattern, Escape)
                                        || EF.Functions.ILike(e.Venue.City, pattern, Escape))));
        }

        // A performer's reach in a year is a few hundred events at most, so the order that puts
        // upcoming before past is taken in memory rather than asked of the database.
        var found = await query.OrderBy(e => e.StartsAt).Take(200).ToListAsync(ct);

        return found
            .OrderBy(e => e.StartsAt < now)
            .ThenBy(e => (e.StartsAt - now).Duration())
            .Take(take)
            .ToList();
    }

    /// <summary>Performers by name. Ones with an upcoming show lead: a name with nothing on sale is rarely the one meant.</summary>
    public async Task<IReadOnlyList<Performer>> PerformersAsync(string text, int take = 6, CancellationToken ct = default)
    {
        var patterns = Patterns(text).ToList();
        if (patterns.Count == 0)
            return [];

        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();

        var query = db.Performers.AsNoTracking();
        foreach (var pattern in patterns)
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, Escape));

        return await query
            .OrderByDescending(p => db.Events.Any(e => e.PerformerId == p.Id && e.StartsAt > now && e.Status != EventStatus.Cancelled))
            .ThenBy(p => p.Name)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Venues by name, city or state.</summary>
    public async Task<IReadOnlyList<Venue>> VenuesAsync(string text, int take = 6, CancellationToken ct = default)
    {
        var patterns = Patterns(text).ToList();
        if (patterns.Count == 0)
            return [];

        await using var db = await factory.CreateDbContextAsync(ct);

        var query = db.Venues.AsNoTracking();
        foreach (var pattern in patterns)
            query = query.Where(v => EF.Functions.ILike(v.Name, pattern, Escape)
                                     || EF.Functions.ILike(v.City, pattern, Escape)
                                     || EF.Functions.ILike(v.State, pattern, Escape));

        return await query.OrderBy(v => v.Name).Take(take).ToListAsync(ct);
    }

    /// <summary>
    /// The same rule in memory, for a list already loaded: every word appears in at least one of
    /// the fields.
    /// </summary>
    public static bool MatchesAllWords(string text, params string?[] fields)
        => Words(text).All(word => fields.Any(f => f is not null && f.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>ILIKE's escape character, named on every call so the rule does not rest on a server default.</summary>
    public const string Escape = @"\";

    /// <summary>One contains-pattern per word, with LIKE's own characters escaped.</summary>
    public static IEnumerable<string> Patterns(string? text)
        => Words(text).Select(w => $"%{w.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%");

    private static string[] Words(string? text)
        => (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
