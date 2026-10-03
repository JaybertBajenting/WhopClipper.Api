using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EditPlanId",
                schema: "clipper",
                table: "PublishJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                schema: "clipper",
                table: "PublishJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotificationScheduledFor",
                schema: "clipper",
                table: "PublishJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_Platform_ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs",
                columns: new[] { "Platform", "ScheduledPublishAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublishJobs_Platform_ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropColumn(
                name: "EditPlanId",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropColumn(
                name: "NotificationScheduledFor",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropColumn(
                name: "ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs");
        }
    }
}
