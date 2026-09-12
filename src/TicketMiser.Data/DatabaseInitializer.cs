using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketMiser.Core.Entities;

namespace TicketMiser.Data;

/// <summary>
/// Applies migrations, keeps partitions ahead of the clock, and seeds reference rows.
/// Safe to run on every start: each step is idempotent.
/// </summary>
public class DatabaseInitializer(TicketMiserDbContext db, ILogger<DatabaseInitializer> logger)
{
    /// <summary>The two partitioned tables, by the name the partition function takes.</summary>
    public static readonly string[] PartitionedTables = ["PriceObservations", "OnSaleTicks"];

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await EnsurePartitionsAsync(ct);
        await SeedCategoriesAsync(ct);
        await SeedSourcesAsync(ct);
        await SeedNashvilleVenuesAsync(ct);
    }

    /// <summary>
    /// Guarantees a partition exists for this month and the next two, on both partitioned
    /// tables. Called at startup and before each ingestion run, so crossing a month boundary
    /// is never an insert failure.
    /// </summary>
    public async Task EnsurePartitionsAsync(CancellationToken ct = default)
    {
        foreach (var table in PartitionedTables)
        {
            for (var offset = 0; offset <= 2; offset++)
            {
                var target = DateTimeOffset.UtcNow.AddMonths(offset);
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT ticketmiser_ensure_partition({0}, {1})", [table, target], ct);
            }
        }
    }

    private async Task SeedCategoriesAsync(CancellationToken ct)
    {
        var wanted = new (string Key, string Name)[]
        {
            ("concert", "Concerts"),
            ("sports", "Sports"),
            ("theater", "Theatre"),
            ("comedy", "Comedy")
        };

        var existing = await db.Categories.Select(c => c.Key).ToListAsync(ct);

        foreach (var (key, name) in wanted.Where(w => !existing.Contains(w.Key)))
            db.Categories.Add(new Category { Key = key, Name = name });

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded categories");
        }
    }

    /// <summary>
    /// Registers the providers with their published ceilings. These numbers are the contract
    /// the budget guard enforces. SeatGeek publishes none, so it is given a conservative ceiling
    /// of our own rather than treated as unlimited.
    /// </summary>
    private async Task SeedSourcesAsync(CancellationToken ct)
    {
        var wanted = new[]
        {
            new Source
            {
                Key = "ticketmaster",
                Name = "Ticketmaster",
                Kind = SourceKind.Primary,
                BaseUrl = "https://app.ticketmaster.com/discovery/v2/",
                RateLimitPerDay = 5000,
                RateLimitPerHour = 5 * 3600,
                Enabled = true
            },
            new Source
            {
                Key = "ticketmaster-inventory",
                Name = "Ticketmaster Inventory Status",
                Kind = SourceKind.Primary,
                BaseUrl = "https://app.ticketmaster.com/inventory-status/v1/",
                Enabled = true
            },
            new Source
            {
                Key = "ticketmaster-feed",
                Name = "Ticketmaster Discovery Feed",
                Kind = SourceKind.Feed,
                BaseUrl = "https://app.ticketmaster.com/discovery-feed/v2/",
                RateLimitPerDay = 4,
                Enabled = true
            },
            new Source
            {
                Key = "seatgeek",
                Name = "SeatGeek",
                Kind = SourceKind.Resale,
                BaseUrl = "https://api.seatgeek.com/2/",
                RateLimitPerHour = 600,
                Enabled = true
            }
        };

        var existing = await db.Sources.Select(s => s.Key).ToListAsync(ct);

        foreach (var source in wanted.Where(s => !existing.Contains(s.Key)))
            db.Sources.Add(source);

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded sources");
        }
    }

    /// <summary>
    /// The rooms and who tickets them, researched 2026-09-11. A venue arriving from a source is
    /// resolved onto one of these by name so the provider is known before the first price.
    /// </summary>
    public static readonly (string Name, string Provider, int? Capacity)[] NashvilleVenues =
    [
        ("Bridgestone Arena", "ticketmaster", 20000),
        ("Nissan Stadium", "ticketmaster", 69143),
        ("Ascend Amphitheater", "ticketmaster", 6800),
        ("FirstBank Amphitheater", "ticketmaster", 7500),
        ("Brooklyn Bowl Nashville", "ticketmaster", 1200),
        ("Marathon Music Works", "ticketmaster", 1500),
        ("Ryman Auditorium", "axs", 2362),
        ("Grand Ole Opry House", "axs", 4400),
        ("The Basement East", "axs", 400),
        ("The Basement", "axs", 150),
        ("Eastside Bowl", "axs", 800),
        ("Exit/In", "etix", 500),
        ("3rd & Lindsley", "etix", 600),
        ("The Caverns", "etix", 1200)
    ];

    private async Task SeedNashvilleVenuesAsync(CancellationToken ct)
    {
        var existing = await db.Venues
            .Where(v => v.City == "Nashville" || v.City == "Madison" || v.City == "Pelham" || v.City == "Franklin")
            .Select(v => v.Name)
            .ToListAsync(ct);

        foreach (var (name, provider, capacity) in NashvilleVenues.Where(v => !existing.Contains(v.Name)))
        {
            var city = name switch
            {
                "Eastside Bowl" => "Madison",
                "The Caverns" => "Pelham",
                "FirstBank Amphitheater" => "Franklin",
                _ => "Nashville"
            };

            db.Venues.Add(new Venue
            {
                Name = name,
                City = city,
                State = "TN",
                CountryCode = "US",
                Timezone = "America/Chicago",
                TicketingProvider = provider,
                Capacity = capacity
            });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded Nashville venues");
        }
    }
}
