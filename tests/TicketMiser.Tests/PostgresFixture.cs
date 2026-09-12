using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketMiser.Data;

namespace TicketMiser.Tests;

/// <summary>
/// A disposable Postgres shared by the integration tests. Real Postgres on purpose: native
/// range partitioning, jsonb and timestamptz do not exist in the in-memory provider, so a
/// green suite there would prove nothing about the schema that ships.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("ticketmiser_test")
        .WithUsername("ticketmiser")
        .WithPassword("ticketmiser_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public TicketMiserDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TicketMiserDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new TicketMiserDbContext(options);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>A clock the tests move by hand. No package needed for what it does.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
