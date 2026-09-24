using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TicketMiser.Core.Entities;

namespace TicketMiser.Data;

public class TicketMiserDbContext(DbContextOptions<TicketMiserDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Performer> Performers => Set<Performer>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Watch> Watches => Set<Watch>();

    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();
    public DbSet<OnSaleTick> OnSaleTicks => Set<OnSaleTick>();
    public DbSet<FinalPrice> FinalPrices => Set<FinalPrice>();

    public DbSet<Purchase> Purchases => Set<Purchase>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<KpiDaily> KpiDailies => Set<KpiDaily>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Incident> Incidents => Set<Incident>();

    // Personal data (legal-guidelines rule 8): excluded from every published snapshot.
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<SignInToken> SignInTokens => Set<SignInToken>();
    public DbSet<ReportSubscription> ReportSubscriptions => Set<ReportSubscription>();
    public DbSet<ReportDelivery> ReportDeliveries => Set<ReportDelivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var jsonOptions = JsonSerializerOptions.Default;

        // External-id maps are stored as jsonb: cross-source entity resolution needs a bag of
        // per-source keys, and the shape differs by provider.
        var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, jsonOptions) ?? new());

        var dictComparer = new ValueComparer<Dictionary<string, string>>(
            (a, c) => JsonSerializer.Serialize(a, jsonOptions) == JsonSerializer.Serialize(c, jsonOptions),
            v => v == null ? 0 : JsonSerializer.Serialize(v, jsonOptions).GetHashCode(),
            v => JsonSerializer.Deserialize<Dictionary<string, string>>(
                     JsonSerializer.Serialize(v, jsonOptions), jsonOptions) ?? new());

        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        b.Entity<Performer>(e =>
        {
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ExternalIds).HasColumnType("jsonb").HasConversion(dictConverter, dictComparer);
        });

        b.Entity<Venue>(e =>
        {
            e.HasIndex(x => new { x.City, x.Name });
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.City).HasMaxLength(128).IsRequired();
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.Property(x => x.TicketingProvider).HasMaxLength(32);
            e.Property(x => x.ExternalIds).HasColumnType("jsonb").HasConversion(dictConverter, dictComparer);
        });

        b.Entity<Event>(e =>
        {
            e.HasIndex(x => x.StartsAt);
            e.HasIndex(x => x.OnSaleAt);
            e.HasIndex(x => new { x.VenueId, x.StartsAt });
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(512).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(EventSlug.MaxLength);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Presales).HasColumnType("jsonb");
            e.Property(x => x.CreatedBySource).HasMaxLength(64);
            e.Property(x => x.ExternalIds).HasColumnType("jsonb").HasConversion(dictConverter, dictComparer);
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Performer).WithMany().HasForeignKey(x => x.PerformerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Source>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.BaseUrl).HasMaxLength(256);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FailureMode).HasMaxLength(32);
        });

        b.Entity<Watch>(e =>
        {
            // One watch per owner per event, the operator's (no owner) included: NULLS NOT
            // DISTINCT, so the operator cannot hold two either. Two owners on one event are two
            // rows and one fetch; the scheduler reads the union (WatchUnion).
            e.HasIndex(x => new { x.OwnerId, x.EventId }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => x.EventId);
            e.Property(x => x.TargetPrice).HasPrecision(12, 2);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PriceObservation>(e =>
        {
            // Composite key led by the partition column: Postgres requires the partition key
            // to participate in every unique constraint on a partitioned table.
            e.HasKey(x => new { x.ObservedAt, x.Id });
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Lowest).HasPrecision(12, 2);
            e.Property(x => x.Average).HasPrecision(12, 2);
            e.Property(x => x.Highest).HasPrecision(12, 2);
            e.Property(x => x.FaceMin).HasPrecision(12, 2);
            e.Property(x => x.FaceMax).HasPrecision(12, 2);

            // "Price history for this event and source, newest first."
            e.HasIndex(x => new { x.EventId, x.SourceId, x.ObservedAt })
                .HasDatabaseName("ix_price_observation_event_source_observed");

            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
        });

        b.Entity<OnSaleTick>(e =>
        {
            e.HasKey(x => new { x.ObservedAt, x.Id });
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.EventStatusCode).HasMaxLength(32);
            e.Property(x => x.PrimaryStatus).HasMaxLength(32);
            e.Property(x => x.ResaleStatus).HasMaxLength(32);
            e.Property(x => x.Lowest).HasPrecision(12, 2);
            e.Property(x => x.Highest).HasPrecision(12, 2);

            // "The record for this event, in order."
            e.HasIndex(x => new { x.EventId, x.ObservedAt })
                .HasDatabaseName("ix_on_sale_tick_event_observed");

            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
        });

        b.Entity<FinalPrice>(e =>
        {
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Lowest).HasPrecision(12, 2);
            e.Property(x => x.Average).HasPrecision(12, 2);
            e.Property(x => x.Highest).HasPrecision(12, 2);

            // One final per event per source. Promotion is idempotent because of this.
            e.HasIndex(x => new { x.EventId, x.SourceId }).IsUnique().HasDatabaseName("ux_final_price");

            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
        });

        b.Entity<Purchase>(e =>
        {
            e.Property(x => x.PaidPerTicket).HasPrecision(12, 2);
            e.Property(x => x.SavingsVsFinal).HasPrecision(12, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1024);
            e.HasIndex(x => x.PurchasedAt);
            e.HasIndex(x => x.OwnerId);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<IngestionRun>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.JobKey).HasMaxLength(64);
            e.HasIndex(x => new { x.SourceId, x.StartedAt });
            e.Ignore(x => x.Duration);
        });

        b.Entity<KpiDaily>(e =>
        {
            e.HasKey(x => new { x.Day, x.SourceId });
        });

        b.Entity<Alert>(e =>
        {
            e.Property(x => x.RuleKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(1024);
            e.HasIndex(x => new { x.RuleKey, x.SourceId, x.EventId, x.ResolvedAt });
            e.Ignore(x => x.IsOpen);
            e.HasOne(x => x.Incident).WithMany(i => i.Alerts).HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Incident>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Timeline).HasColumnType("jsonb");
            e.Ignore(x => x.TimeToResolve);
        });

        b.Entity<Subscription>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(Subscription.EmailMaxLength).IsRequired();
            e.Property(x => x.ConfirmToken).HasMaxLength(Subscription.TokenMaxLength).IsRequired();
            e.Property(x => x.UnsubscribeToken).HasMaxLength(Subscription.TokenMaxLength).IsRequired();
            e.Ignore(x => x.IsActive);

            // One row per person per event; a second subscribe reuses it.
            e.HasIndex(x => new { x.Email, x.EventId }).IsUnique();
            e.HasIndex(x => x.ConfirmToken).IsUnique();
            e.HasIndex(x => x.UnsubscribeToken).IsUnique();
            // "Who is waiting on this event": the delivery service's read.
            e.HasIndex(x => x.EventId);

            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            // An account's deletion takes the alerts it asked for with it.
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertDelivery>(e =>
        {
            e.Property(x => x.ProviderMessageId).HasMaxLength(128);

            // The idempotency guarantee: one alert reaches one subscriber once.
            e.HasIndex(x => new { x.AlertId, x.SubscriptionId }).IsUnique().HasDatabaseName("ux_alert_delivery");

            e.HasOne(x => x.Alert).WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        // The monthly report's own list (rule 8): not the event alerts' list, and never mixed with it.
        b.Entity<ReportSubscription>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(Subscription.EmailMaxLength).IsRequired();
            e.Property(x => x.ConfirmToken).HasMaxLength(Subscription.TokenMaxLength).IsRequired();
            e.Property(x => x.UnsubscribeToken).HasMaxLength(Subscription.TokenMaxLength).IsRequired();
            e.Ignore(x => x.IsActive);

            // One row per address; a second subscribe reuses it.
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.ConfirmToken).IsUnique();
            e.HasIndex(x => x.UnsubscribeToken).IsUnique();

            // An account's deletion takes its place on the list with it.
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReportDelivery>(e =>
        {
            e.Property(x => x.Month).HasMaxLength(ReportDelivery.MonthLength).IsFixedLength().IsRequired();
            e.Property(x => x.ProviderMessageId).HasMaxLength(128);

            // The idempotency guarantee: one month's report reaches one subscriber once.
            e.HasIndex(x => new { x.ReportSubscriptionId, x.Month }).IsUnique().HasDatabaseName("ux_report_delivery");

            e.HasOne(x => x.ReportSubscription).WithMany().HasForeignKey(x => x.ReportSubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Account>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(Subscription.EmailMaxLength).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<SignInToken>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(Subscription.EmailMaxLength).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(SignInToken.HashLength).IsFixedLength().IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            // "Was a link sent to this address a moment ago": the resend throttle's read.
            e.HasIndex(x => new { x.Email, x.CreatedAt });
        });
    }
}
