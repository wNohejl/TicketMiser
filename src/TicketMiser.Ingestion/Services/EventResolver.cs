using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketMiser.Core.Contracts;
using TicketMiser.Core.Entities;
using TicketMiser.Data;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Resolves provider-specific identifiers onto our canonical rows.
///
/// <para>
/// Fast path on the source's own id, slow path on venue plus name or performer plus a start
/// inside one fixture's drift, and the discovered id is recorded so the next run takes the
/// fast path. Two lessons from LineOps are built in: the window is one fixture's drift, not a
/// day, and only the schedule authority may move a start time. Here the authority is the
/// primary market or the feed; a resale site is not.
/// </para>
///
/// <para>
/// Venue is never optional in the slow path. The Ryman and the Opry House can host the same
/// performer on the same night, and the venue is what tells them apart.
/// </para>
/// </summary>
public class EventResolver(TicketMiserDbContext db)
{
    public static readonly TimeSpan SameFixtureDrift = TimeSpan.FromHours(6);

    private readonly Dictionary<string, SourceKind?> _sourceKindCache = [];
    private readonly Dictionary<(string SourceKey, string Reference), Venue> _venueCache = [];
    private readonly Dictionary<(string SourceKey, string Reference), Performer> _performerCache = [];

    public async Task<Venue> ResolveVenueAsync(string sourceKey, CanonicalVenueRef reference, CancellationToken ct)
    {
        var cacheKey = (sourceKey, reference.SourceVenueId ?? reference.Name);
        if (_venueCache.TryGetValue(cacheKey, out var hit))
            return hit;

        var normalised = Normalise(reference.Name);
        var city = reference.City;

        var candidates = await db.Venues
            .Where(v => city == null || v.City == city || v.State == reference.State)
            .ToListAsync(ct);

        Venue? venue = null;

        if (reference.SourceVenueId is { } id)
            venue = candidates.FirstOrDefault(v => v.ExternalIds.TryGetValue(sourceKey, out var known) && known == id);

        // The seeded Nashville list is matched by name so the ticketing provider is known
        // before the first price arrives.
        venue ??= candidates.FirstOrDefault(v => Normalise(v.Name) == normalised);

        if (venue is null)
        {
            venue = new Venue
            {
                Name = reference.Name,
                City = reference.City ?? string.Empty,
                State = reference.State ?? string.Empty,
                CountryCode = reference.CountryCode ?? "US",
                Timezone = reference.Timezone
            };
            db.Venues.Add(venue);
        }
        else
        {
            if (venue.Timezone is null && reference.Timezone is not null)
                venue.Timezone = reference.Timezone;
            if (venue.City.Length == 0 && reference.City is not null)
                venue.City = reference.City;
        }

        if (reference.SourceVenueId is { } sourceId
            && (!venue.ExternalIds.TryGetValue(sourceKey, out var existing) || existing != sourceId))
        {
            venue.ExternalIds = new Dictionary<string, string>(venue.ExternalIds) { [sourceKey] = sourceId };
        }

        await db.SaveChangesAsync(ct);
        _venueCache[cacheKey] = venue;
        return venue;
    }

    public async Task<Performer> ResolvePerformerAsync(string sourceKey, CanonicalPerformerRef reference, CancellationToken ct)
    {
        var cacheKey = (sourceKey, reference.SourcePerformerId ?? reference.Name);
        if (_performerCache.TryGetValue(cacheKey, out var hit))
            return hit;

        var normalised = Normalise(reference.Name);
        var all = await db.Performers.ToListAsync(ct);

        Performer? performer = null;

        if (reference.SourcePerformerId is { } id)
            performer = all.FirstOrDefault(p => p.ExternalIds.TryGetValue(sourceKey, out var known) && known == id);

        performer ??= all.FirstOrDefault(p => Normalise(p.Name) == normalised);

        if (performer is null)
        {
            performer = new Performer { Name = reference.Name };
            db.Performers.Add(performer);
        }

        if (reference.SourcePerformerId is { } sourceId
            && (!performer.ExternalIds.TryGetValue(sourceKey, out var existing) || existing != sourceId))
        {
            performer.ExternalIds = new Dictionary<string, string>(performer.ExternalIds) { [sourceKey] = sourceId };
        }

        await db.SaveChangesAsync(ct);
        _performerCache[cacheKey] = performer;
        return performer;
    }

    public async Task<Event> ResolveEventAsync(string sourceKey, CanonicalEvent canonical, CancellationToken ct)
    {
        var venue = await ResolveVenueAsync(sourceKey, canonical.Venue, ct);

        var performer = canonical.Performer is { } performerRef
            ? await ResolvePerformerAsync(sourceKey, performerRef, ct)
            : null;

        var category = canonical.CategoryKey is { } categoryKey
            ? await db.Categories.FirstOrDefaultAsync(c => c.Key == categoryKey, ct)
            : null;

        // Fast path: this provider has named this fixture before.
        var candidates = await db.Events
            .Where(e => e.VenueId == venue.Id
                        && e.StartsAt > canonical.StartsAt.AddDays(-2)
                        && e.StartsAt < canonical.StartsAt.AddDays(2))
            .ToListAsync(ct);

        var evt = candidates.FirstOrDefault(e =>
            e.ExternalIds.TryGetValue(sourceKey, out var id) && id == canonical.SourceEventId);

        var knownToThisProvider = evt is not null;

        if (evt is null)
        {
            // Slow path: the same fixture as a different provider named it. Same venue, a start
            // inside one fixture's drift, and either the same performer or the same normalised
            // name; never a row this provider has already given a different id.
            var normalisedName = Normalise(canonical.Name);

            evt = candidates
                .Where(e => (e.StartsAt - canonical.StartsAt).Duration() < SameFixtureDrift)
                .Where(e => (performer is not null && e.PerformerId == performer.Id)
                            || Normalise(e.Name) == normalisedName)
                .Where(e => !(e.ExternalIds.TryGetValue(sourceKey, out var other) && other != canonical.SourceEventId))
                .OrderBy(e => (e.StartsAt - canonical.StartsAt).Duration())
                .FirstOrDefault();

            if (evt is null)
            {
                evt = new Event
                {
                    Name = canonical.Name,
                    VenueId = venue.Id,
                    PerformerId = performer?.Id,
                    CategoryId = category?.Id,
                    StartsAt = canonical.StartsAt,
                    Status = MapStatus(canonical.Status),
                    OnSaleAt = canonical.OnSaleAt,
                    OnSaleTbd = canonical.OnSaleTbd,
                    Presales = SerialisePresales(canonical.PresaleWindows),
                    CreatedBySource = sourceKey
                };
                db.Events.Add(evt);
            }

            evt.ExternalIds = new Dictionary<string, string>(evt.ExternalIds)
            {
                [sourceKey] = canonical.SourceEventId
            };

            await db.SaveChangesAsync(ct);
        }

        var changed = false;

        // Only a schedule authority moves a start time or an on-sale time, and only about a
        // fixture it has already named.
        if (knownToThisProvider && await IsScheduleAuthorityAsync(sourceKey, ct))
        {
            if (evt!.StartsAt != canonical.StartsAt)
            {
                evt.StartsAt = canonical.StartsAt;
                changed = true;
            }

            if (canonical.OnSaleAt is { } onSale && evt.OnSaleAt != onSale)
            {
                evt.OnSaleAt = onSale;
                changed = true;
            }

            if (evt.OnSaleTbd != canonical.OnSaleTbd)
            {
                evt.OnSaleTbd = canonical.OnSaleTbd;
                changed = true;
            }

            var presales = SerialisePresales(canonical.PresaleWindows);
            if (canonical.PresaleWindows.Count > 0 && evt.Presales != presales)
            {
                evt.Presales = presales;
                changed = true;
            }

            var status = MapStatus(canonical.Status);
            if (canonical.Status is not null && evt.Status != status)
            {
                evt.Status = status;
                changed = true;
            }
        }
        else if (evt!.OnSaleAt is null && canonical.OnSaleAt is { } firstOnSale)
        {
            // A gap is filled by anyone; a value is only moved by the authority.
            evt.OnSaleAt = firstOnSale;
            changed = true;
        }

        if (evt.PerformerId is null && performer is not null)
        {
            evt.PerformerId = performer.Id;
            changed = true;
        }

        if (evt.CategoryId is null && category is not null)
        {
            evt.CategoryId = category.Id;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);

        return evt;
    }

    /// <summary>Primary and feed sources are believed about the schedule; resale sources are not.</summary>
    private async Task<bool> IsScheduleAuthorityAsync(string sourceKey, CancellationToken ct)
    {
        if (!_sourceKindCache.TryGetValue(sourceKey, out var kind))
        {
            kind = await db.Sources
                .Where(s => s.Key == sourceKey)
                .Select(s => (SourceKind?)s.Kind)
                .FirstOrDefaultAsync(ct);

            _sourceKindCache[sourceKey] = kind;
        }

        return kind != SourceKind.Resale;
    }

    public static EventStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "rescheduled" => EventStatus.Rescheduled,
        "postponed" => EventStatus.Postponed,
        "cancelled" or "canceled" => EventStatus.Cancelled,
        _ => EventStatus.Scheduled
    };

    private static string SerialisePresales(IReadOnlyList<CanonicalPresale> presales)
        => JsonSerializer.Serialize(presales.Select(p => new { name = p.Name, startsAt = p.StartsAt, endsAt = p.EndsAt }));

    private static string Normalise(string name)
        => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
