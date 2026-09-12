using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketMiser.Data;

/// <summary>
/// Used only by `dotnet ef` at design time so migrations can be created without booting a
/// host. Runtime connection strings come from configuration.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TicketMiserDbContext>
{
    public TicketMiserDbContext CreateDbContext(string[] args)
    {
        // No embedded credential: `dotnet ef` runs against whatever TICKETMISER_CONNECTION
        // points at, and Npgsql picks up PGPASSWORD if the password is supplied that way.
        var connectionString = Environment.GetEnvironmentVariable("TICKETMISER_CONNECTION")
            ?? "Host=localhost;Port=5434;Database=ticketmiser;Username=ticketmiser";

        var options = new DbContextOptionsBuilder<TicketMiserDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TicketMiserDbContext(options);
    }
}
