using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketMiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Events",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_Slug",
                table: "Events",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_Slug",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Events");
        }
    }
}
