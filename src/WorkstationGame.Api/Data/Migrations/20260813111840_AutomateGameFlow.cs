using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkstationGame.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomateGameFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevealDurationSeconds",
                table: "Games",
                type: "integer",
                nullable: false,
                defaultValue: 8);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PausedAt",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "RevealDurationSeconds",
                table: "Games");
        }
    }
}
