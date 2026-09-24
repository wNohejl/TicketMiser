using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketMiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfirmationSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnsubscribeToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UnsubscribedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportSubscriptions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportSubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportDeliveries_ReportSubscriptions_ReportSubscriptionId",
                        column: x => x.ReportSubscriptionId,
                        principalTable: "ReportSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_report_delivery",
                table: "ReportDeliveries",
                columns: new[] { "ReportSubscriptionId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_AccountId",
                table: "ReportSubscriptions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_ConfirmToken",
                table: "ReportSubscriptions",
                column: "ConfirmToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_Email",
                table: "ReportSubscriptions",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_UnsubscribeToken",
                table: "ReportSubscriptions",
                column: "UnsubscribeToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportDeliveries");

            migrationBuilder.DropTable(
                name: "ReportSubscriptions");
        }
    }
}
