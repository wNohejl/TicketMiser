using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketMiser.Data.Migrations
{
    /// <summary>
    /// Accounts without passwords, and an owner on every watch and purchase. Every existing
    /// row keeps a null owner, which is the operator's, so the single-operator desk reads what
    /// it read before. No subscription is moved here: no account exists yet. A Phase 6
    /// subscription becomes its address's owned watch when that address first signs in
    /// (AccountService.ClaimSubscriptionsAsync).
    /// </summary>
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watches_EventId",
                table: "Watches");

            migrationBuilder.AddColumn<int>(
                name: "OwnerId",
                table: "Watches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnerId",
                table: "Purchases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignInTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Watches_EventId",
                table: "Watches",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_OwnerId_EventId",
                table: "Watches",
                columns: new[] { "OwnerId", "EventId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AccountId",
                table: "Subscriptions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_OwnerId",
                table: "Purchases",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignInTokens_Email_CreatedAt",
                table: "SignInTokens",
                columns: new[] { "Email", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignInTokens_TokenHash",
                table: "SignInTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_Accounts_OwnerId",
                table: "Purchases",
                column: "OwnerId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Watches_Accounts_OwnerId",
                table: "Watches",
                column: "OwnerId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without an owner column a fan's watch or purchase would read as the operator's.
            // Going back removes them rather than hand them to the desk.
            migrationBuilder.Sql("DELETE FROM \"Watches\" WHERE \"OwnerId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM \"Purchases\" WHERE \"OwnerId\" IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_Accounts_OwnerId",
                table: "Purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_Watches_Accounts_OwnerId",
                table: "Watches");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "SignInTokens");

            migrationBuilder.DropIndex(
                name: "IX_Watches_EventId",
                table: "Watches");

            migrationBuilder.DropIndex(
                name: "IX_Watches_OwnerId_EventId",
                table: "Watches");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_AccountId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_OwnerId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Purchases");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_EventId",
                table: "Watches",
                column: "EventId",
                unique: true);
        }
    }
}
