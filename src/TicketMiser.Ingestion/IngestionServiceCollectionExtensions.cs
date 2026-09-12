using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Ingestion.Adapters;
using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;

namespace TicketMiser.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ingestion pipeline. Adapters are registered only when configured with a
    /// key, so a cold clone with no keys still starts: it has no source registered, which the
    /// pull menu and the reliability layer both read as unconfigured rather than broken.
    /// </summary>
    public static IServiceCollection AddTicketMiserIngestion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));
        services.PostConfigure<IngestionOptions>(o =>
        {
            // The reserve is the margin for a retry; below a fifth the guard becomes routine.
            if (o.PricePolling.ReservePercent < 20)
                o.PricePolling.ReservePercent = 20;
        });

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<EventResolver>();
        services.AddScoped<CreditBudgetGuard>();
        services.AddScoped<DiscoveryService>();
        services.AddScoped<PriceIngestionService>();
        services.AddScoped<OnSaleWatchService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<PricePollPlanner>();
        services.AddScoped<SourceRegistry>();

        // Singleton: it creates a scope per job, so both the scheduler and the pull menu hold it.
        services.AddSingleton<IngestionJobs>();

        // Runs once at start, after the initialiser has seeded the reference rows.
        services.AddHostedService<ReferenceReconciler>();

        var options = configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()
                      ?? new IngestionOptions();

        var tmKey = options.Ticketmaster.ApiKey;

        if (options.Ticketmaster.Enabled && !string.IsNullOrWhiteSpace(tmKey))
        {
            services.AddHttpClient<TicketmasterDiscoveryAdapter>((sp, client) =>
                {
                    client.BaseAddress = new Uri("https://app.ticketmaster.com/discovery/v2/");
                    client.Timeout = TimeSpan.FromSeconds(30);
                    ApplyUserAgent(client, options.Ticketmaster.EffectiveUserAgent, sp);
                })
                // Retry with jittered backoff, a circuit breaker on sustained failure, and
                // per-attempt timeouts. A tripped breaker is what the reliability layer
                // surfaces as a degraded source.
                .AddStandardResilienceHandler();

            services.AddScoped<IPriceSource>(sp => sp.GetRequiredService<TicketmasterDiscoveryAdapter>());

            if (options.DiscoveryFeed.Enabled)
            {
                services.AddHttpClient<TicketmasterFeedAdapter>((sp, client) =>
                    {
                        client.BaseAddress = new Uri("https://app.ticketmaster.com/discovery-feed/v2/");
                        // A national file over a slow link is minutes, not seconds.
                        client.Timeout = TimeSpan.FromMinutes(10);
                        ApplyUserAgent(client, options.Ticketmaster.EffectiveUserAgent, sp);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    });

                services.AddScoped<IPriceSource>(sp => sp.GetRequiredService<TicketmasterFeedAdapter>());
            }
        }

        var inventoryKey = options.TicketmasterInventory.ApiKey;

        if (options.TicketmasterInventory.Enabled && !string.IsNullOrWhiteSpace(inventoryKey))
        {
            services.AddHttpClient<TicketmasterInventoryAdapter>((sp, client) =>
                {
                    client.BaseAddress = new Uri("https://app.ticketmaster.com/inventory-status/v1/");
                    client.Timeout = TimeSpan.FromSeconds(30);
                    ApplyUserAgent(client, options.TicketmasterInventory.EffectiveUserAgent, sp);
                })
                .AddStandardResilienceHandler();

            services.AddScoped<IAvailabilitySource>(sp => sp.GetRequiredService<TicketmasterInventoryAdapter>());
        }

        if (options.SeatGeek.Enabled && !string.IsNullOrWhiteSpace(options.SeatGeek.ClientId))
        {
            services.AddHttpClient<SeatGeekAdapter>((sp, client) =>
                {
                    client.BaseAddress = new Uri("https://api.seatgeek.com/2/");
                    client.Timeout = TimeSpan.FromSeconds(30);
                    ApplyUserAgent(client, options.SeatGeek.EffectiveUserAgent, sp);
                })
                .AddStandardResilienceHandler();

            services.AddScoped<IPriceSource>(sp => sp.GetRequiredService<SeatGeekAdapter>());
        }

        return services;
    }

    /// <summary>
    /// Adds the unattended scheduler. Separate so the web app can trigger jobs manually without
    /// also owning the schedule, which is what lets the worker be its own process.
    /// </summary>
    public static IServiceCollection AddTicketMiserIngestionScheduler(this IServiceCollection services)
    {
        services.AddHostedService<IngestionScheduler>();
        return services;
    }

    /// <summary>
    /// Sets a client's identification, correcting an unusable override rather than obeying it.
    /// An absent User-Agent is what took ESPN down for LineOps; a typo must not reproduce it.
    /// </summary>
    private static void ApplyUserAgent(HttpClient client, string configured, IServiceProvider services)
    {
        if (client.DefaultRequestHeaders.UserAgent.TryParseAdd(configured))
            return;

        services.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(IngestionServiceCollectionExtensions))
            .LogWarning("Configured User-Agent '{Configured}' is not a valid header value; using '{Fallback}'.",
                configured, SourceOptions.DefaultUserAgent);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(SourceOptions.DefaultUserAgent);
    }

    public static IngestionOptions GetIngestionOptions(this IServiceProvider provider)
        => provider.GetRequiredService<IOptions<IngestionOptions>>().Value;
}
