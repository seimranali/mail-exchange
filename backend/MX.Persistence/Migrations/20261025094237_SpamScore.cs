using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MX.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpamScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "spam_score",
                schema: "mx",
                table: "mail_messages",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "spam_score",
                schema: "mx",
                table: "mail_messages");
        }
    }
}
