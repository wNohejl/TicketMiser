using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TicketMiser.Data;

public static class ServiceCollectionExtensions
{
    public const string ConnectionStringName = "TicketMiser";

    public static IServiceCollection AddTicketMiserData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"No '{ConnectionStringName}' connection string configured. Set " +
                "ConnectionStrings__TicketMiser (compose does this from .env), or use " +
                "`dotnet user-secrets` for host-side runs.");

        // Blazor Server components outlive a request, so they take a factory and own the
        // context lifetime per operation. Background services and the ingestion pipeline still
        // want a scoped context, so one is resolved from the same factory.
        services.AddDbContextFactory<TicketMiserDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3)));

        services.AddScoped<TicketMiserDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<TicketMiserDbContext>>().CreateDbContext());

        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
