using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketMiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Timeline = table.Column<string>(type: "jsonb", nullable: false),
                    RootCause = table.Column<string>(type: "text", nullable: true),
                    CorrectiveActions = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Performers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalIds = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Performers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RateLimitPerHour = table.Column<int>(type: "integer", nullable: true),
                    RateLimitPerDay = table.Column<int>(type: "integer", nullable: true),
                    MonthlyCreditBudget = table.Column<int>(type: "integer", nullable: true),
                    FailureMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Venues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    City = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TicketingProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Capacity = table.Column<int>(type: "integer", nullable: true),
                    ExternalIds = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Venues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    JobKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RowsIngested = table.Column<int>(type: "integer", nullable: false),
                    RequestsMade = table.Column<int>(type: "integer", nullable: false),
                    CreditsSpent = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionRuns_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KpiDailies",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    FreshnessMinutes = table.Column<double>(type: "double precision", nullable: false),
                    SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                    RowsIngested = table.Column<int>(type: "integer", nullable: false),
                    RunCount = table.Column<int>(type: "integer", nullable: false),
                    ApiCreditsUsed = table.Column<int>(type: "integer", nullable: false),
                    RequestsMade = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KpiDailies", x => new { x.Day, x.SourceId });
                    table.ForeignKey(
                        name: "FK_KpiDailies_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: true),
                    PerformerId = table.Column<int>(type: "integer", nullable: true),
                    VenueId = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OnSaleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OnSaleTbd = table.Column<bool>(type: "boolean", nullable: false),
                    Presales = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBySource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExternalIds = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Events_Performers_PerformerId",
                        column: x => x.PerformerId,
                        principalTable: "Performers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Events_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IncidentId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Alerts_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Alerts_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FinalPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Lowest = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Average = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Highest = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    ListingCount = table.Column<int>(type: "integer", nullable: true),
                    AllIn = table.Column<bool>(type: "boolean", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinalPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinalPrices_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FinalPrices_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The two observation tables are created by hand rather than through CreateTable
            // because they are native range-partitioned tables. Both grow without bound: the
            // observation stream appends a row per event per source per move, and the on-sale
            // record appends a row per event per source per tick and is never pruned. Partitioned
            // by month on ObservedAt, old months of the stream can be dropped as a metadata
            // operation, and a query for one event's history is pruned to the partitions that
            // can hold it.
            migrationBuilder.Sql("""
                CREATE TABLE "PriceObservations" (
                    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
                    "ObservedAt" timestamp with time zone NOT NULL,
                    "EventId" integer NOT NULL,
                    "SourceId" integer NOT NULL,
                    "Currency" character varying(3) NOT NULL,
                    "Lowest" numeric(12,2) NULL,
                    "Average" numeric(12,2) NULL,
                    "Highest" numeric(12,2) NULL,
                    "ListingCount" integer NULL,
                    "AllIn" boolean NOT NULL,
                    "FaceMin" numeric(12,2) NULL,
                    "FaceMax" numeric(12,2) NULL,
                    "IngestionRunId" bigint NOT NULL,
                    CONSTRAINT "PK_PriceObservations" PRIMARY KEY ("ObservedAt", "Id"),
                    CONSTRAINT "FK_PriceObservations_Events_EventId" FOREIGN KEY ("EventId")
                        REFERENCES "Events" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_PriceObservations_Sources_SourceId" FOREIGN KEY ("SourceId")
                        REFERENCES "Sources" ("Id") ON DELETE CASCADE
                ) PARTITION BY RANGE ("ObservedAt");

                CREATE TABLE "OnSaleTicks" (
                    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
                    "ObservedAt" timestamp with time zone NOT NULL,
                    "EventId" integer NOT NULL,
                    "SourceId" integer NOT NULL,
                    "MinutesFromOnSale" integer NOT NULL,
                    "EventStatusCode" character varying(32) NULL,
                    "PrimaryStatus" character varying(32) NULL,
                    "ResaleStatus" character varying(32) NULL,
                    "Currency" character varying(3) NOT NULL,
                    "Lowest" numeric(12,2) NULL,
                    "Highest" numeric(12,2) NULL,
                    "ListingCount" integer NULL,
                    "AllIn" boolean NULL,
                    "IngestionRunId" bigint NOT NULL,
                    CONSTRAINT "PK_OnSaleTicks" PRIMARY KEY ("ObservedAt", "Id"),
                    CONSTRAINT "FK_OnSaleTicks_Events_EventId" FOREIGN KEY ("EventId")
                        REFERENCES "Events" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_OnSaleTicks_Sources_SourceId" FOREIGN KEY ("SourceId")
                        REFERENCES "Sources" ("Id") ON DELETE CASCADE
                ) PARTITION BY RANGE ("ObservedAt");
                """);

            // Creates the monthly partition covering a given timestamp on either table, if
            // absent. The initialiser calls this ahead of each run so a month boundary never
            // turns into a failed insert.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ticketmiser_ensure_partition(parent text, target timestamptz)
                RETURNS void AS $$
                DECLARE
                    period_start date := date_trunc('month', target AT TIME ZONE 'UTC')::date;
                    period_end   date := (date_trunc('month', target AT TIME ZONE 'UTC') + interval '1 month')::date;
                    partition_name text := format('%s_%s', parent, to_char(period_start, 'YYYY_MM'));
                BEGIN
                    IF parent NOT IN ('PriceObservations', 'OnSaleTicks') THEN
                        RAISE EXCEPTION 'unknown partitioned table %', parent;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = partition_name) THEN
                        EXECUTE format(
                            'CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                            partition_name, parent, period_start, period_end);
                    END IF;
                END;
                $$ LANGUAGE plpgsql;
                """);

            // Seed the current month and its neighbours, then a DEFAULT partition on each so a
            // row can never be rejected for falling outside every declared range.
            migrationBuilder.Sql("""
                SELECT ticketmiser_ensure_partition('PriceObservations', now() - interval '1 month');
                SELECT ticketmiser_ensure_partition('PriceObservations', now());
                SELECT ticketmiser_ensure_partition('PriceObservations', now() + interval '1 month');
                CREATE TABLE "PriceObservations_default" PARTITION OF "PriceObservations" DEFAULT;
                SELECT ticketmiser_ensure_partition('OnSaleTicks', now() - interval '1 month');
                SELECT ticketmiser_ensure_partition('OnSaleTicks', now());
                SELECT ticketmiser_ensure_partition('OnSaleTicks', now() + interval '1 month');
                CREATE TABLE "OnSaleTicks_default" PARTITION OF "OnSaleTicks" DEFAULT;
                """);
            migrationBuilder.CreateTable(
                name: "Purchases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PaidPerTicket = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AllIn = table.Column<bool>(type: "boolean", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SavingsVsFinal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Purchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Purchases_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Purchases_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Watches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    NotifyOnDrop = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Watches_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_EventId",
                table: "Alerts",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IncidentId",
                table: "Alerts",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RuleKey_SourceId_EventId_ResolvedAt",
                table: "Alerts",
                columns: new[] { "RuleKey", "SourceId", "EventId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_SourceId",
                table: "Alerts",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Key",
                table: "Categories",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CategoryId",
                table: "Events",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_OnSaleAt",
                table: "Events",
                column: "OnSaleAt");

            migrationBuilder.CreateIndex(
                name: "IX_Events_PerformerId",
                table: "Events",
                column: "PerformerId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_StartsAt",
                table: "Events",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_Events_VenueId_StartsAt",
                table: "Events",
                columns: new[] { "VenueId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FinalPrices_SourceId",
                table: "FinalPrices",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "ux_final_price",
                table: "FinalPrices",
                columns: new[] { "EventId", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_SourceId_StartedAt",
                table: "IngestionRuns",
                columns: new[] { "SourceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KpiDailies_SourceId",
                table: "KpiDailies",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "ix_on_sale_tick_event_observed",
                table: "OnSaleTicks",
                columns: new[] { "EventId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OnSaleTicks_SourceId",
                table: "OnSaleTicks",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Performers_Name",
                table: "Performers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "ix_price_observation_event_source_observed",
                table: "PriceObservations",
                columns: new[] { "EventId", "SourceId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceObservations_SourceId",
                table: "PriceObservations",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_EventId",
                table: "Purchases",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_PurchasedAt",
                table: "Purchases",
                column: "PurchasedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SourceId",
                table: "Purchases",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Key",
                table: "Sources",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Venues_City_Name",
                table: "Venues",
                columns: new[] { "City", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Watches_EventId",
                table: "Watches",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "FinalPrices");

            migrationBuilder.DropTable(
                name: "IngestionRuns");

            migrationBuilder.DropTable(
                name: "KpiDailies");

            migrationBuilder.DropTable(
                name: "OnSaleTicks");

            migrationBuilder.DropTable(
                name: "PriceObservations");

            migrationBuilder.DropTable(
                name: "Purchases");

            migrationBuilder.DropTable(
                name: "Watches");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Performers");

            migrationBuilder.DropTable(
                name: "Venues");
        }
    }
}
