using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TicketMiser.Core.Entities;
using TicketMiser.Data;
using TicketMiser.Data.Changes;

namespace TicketMiser.Tests.Integration;

/// <summary>
/// A save is announced on the change channel, naming the topics it touched, so a window in
/// another process hears about it. Against real Postgres, because LISTEN/NOTIFY is the point.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChangeNotifierTests(PostgresFixture fixture)
{
    private TicketMiserDbContext NotifyingContext()
        => new(new DbContextOptionsBuilder<TicketMiserDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ChangeNotifier(NullLogger<ChangeNotifier>.Instance))
            .Options);

    private async Task<(NpgsqlConnection Connection, List<string> Heard)> ListenAsync()
    {
        var heard = new List<string>();
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        connection.Notification += (_, e) => { lock (heard) heard.Add(e.Payload); };

        await using var listen = new NpgsqlCommand($"LISTEN {DataTopics.Channel}", connection);
        await listen.ExecuteNonQueryAsync();

        return (connection, heard);
    }

    private static async Task<List<string>> DrainAsync(NpgsqlConnection connection, List<string> heard)
    {
        // Notices arrive asynchronously; give the server a moment, then read what came.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            while (true)
                await connection.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        lock (heard)
            return [.. heard];
    }

    [Fact]
    public async Task A_venue_saved_is_announced_as_events()
    {
        var (connection, heard) = await ListenAsync();
        await using var _ = connection;

        await using (var db = NotifyingContext())
        {
            db.Venues.Add(new Venue { Name = $"Notifier Room {Guid.NewGuid():N}", City = "Nashville", State = "TN" });
            await db.SaveChangesAsync();
        }

        var payloads = await DrainAsync(connection, heard);

        Assert.Contains(payloads, p => p.Split(',').Contains(DataTopics.Events));
    }

    [Fact]
    public async Task A_save_inside_a_rolled_back_transaction_is_not_announced()
    {
        var (connection, heard) = await ListenAsync();
        await using var _ = connection;

        var name = $"Rolled Back Room {Guid.NewGuid():N}";

        await using (var db = NotifyingContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Venues.Add(new Venue { Name = name, City = "Nashville", State = "TN" });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Empty(await DrainAsync(connection, heard));
    }

    [Fact]
    public async Task A_save_that_touches_nothing_watched_says_nothing()
    {
        var (connection, heard) = await ListenAsync();
        await using var _ = connection;

        await using (var db = NotifyingContext())
        {
            db.Accounts.Add(new Account { Email = $"notifier-{Guid.NewGuid():N}@example.com", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await DrainAsync(connection, heard));
    }

    [Theory]
    [InlineData(typeof(Event), DataTopics.Events)]
    [InlineData(typeof(Performer), DataTopics.Events)]
    [InlineData(typeof(Venue), DataTopics.Events)]
    [InlineData(typeof(PriceObservation), DataTopics.Prices)]
    [InlineData(typeof(OnSaleTick), DataTopics.Prices)]
    [InlineData(typeof(FinalPrice), DataTopics.Prices)]
    [InlineData(typeof(Watch), DataTopics.Watches)]
    [InlineData(typeof(Purchase), DataTopics.Purchases)]
    [InlineData(typeof(Alert), DataTopics.Alerts)]
    [InlineData(typeof(Incident), DataTopics.Alerts)]
    [InlineData(typeof(IngestionRun), DataTopics.Runs)]
    [InlineData(typeof(Source), null)]
    [InlineData(typeof(Account), null)]
    public void Each_entity_belongs_to_the_topic_a_window_would_watch(Type entity, string? topic)
        => Assert.Equal(topic, DataTopics.For(entity));
}
