using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketMiser.Reliability.Accounts;
using TicketMiser.Reliability.Notifications;

namespace TicketMiser.Reliability;

public static class ReliabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared reliability layer. Both the web app and the ingestion worker
    /// take this same library, which is why KPIs, alerting and incidents behave identically
    /// wherever they are observed.
    /// </summary>
    public static IServiceCollection AddTicketMiserReliability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ReliabilityOptions>(configuration.GetSection(ReliabilityOptions.SectionName));

        services.AddScoped<KpiCalculator>();
        services.AddScoped<BudgetCalculator>();
        services.AddScoped<AlertEngine>();
        services.AddScoped<IncidentService>();

        services.AddTicketMiserNotifications(configuration);

        return services;
    }

    /// <summary>
    /// Email: subscriptions, alert delivery, sign-in links, and exactly one <see cref="INotifier"/>. Postmark
    /// only when <c>Notifications:Provider</c> is <c>postmark</c> and a server token is set;
    /// otherwise the pickup directory, which sends nothing to the internet. Decided once, at
    /// startup, so a running host never changes how it sends.
    /// </summary>
    public static IServiceCollection AddTicketMiserNotifications(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(NotificationOptions.SectionName);
        services.Configure<NotificationOptions>(section);
        services.TryAddSingleton(TimeProvider.System);

        var notifications = section.Get<NotificationOptions>() ?? new NotificationOptions();

        if (notifications.UsesPostmark)
        {
            // No resilience handler: a retried POST is a second email (see PostmarkNotifier).
            services.AddHttpClient<INotifier, PostmarkNotifier>(http =>
            {
                http.BaseAddress = new Uri(PostmarkNotifier.BaseAddress);
                http.Timeout = TimeSpan.FromSeconds(20);
            });
        }
        else
        {
            services.AddSingleton<INotifier, PickupDirectoryNotifier>();
        }

        services.AddScoped<SubscriptionService>();
        services.AddScoped<AlertDeliveryService>();

        // Magic-link sign-in sends through the same notifier: the link is an email like any other.
        services.AddScoped<AccountService>();

        return services;
    }

    /// <summary>Adds the periodic evaluator. Kept separate so a process can consume the
    /// reliability data without also being the thing that produces it.</summary>
    public static IServiceCollection AddTicketMiserReliabilityEvaluator(this IServiceCollection services)
    {
        services.AddHostedService<ReliabilityEvaluator>();
        return services;
    }
}
