using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubePages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_Platform",
                schema: "clipper",
                table: "PlatformConnections");

            migrationBuilder.AddColumn<Guid>(
                name: "ConnectionId",
                schema: "clipper",
                table: "PublishJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "YouTubeConnectionId",
                schema: "clipper",
                table: "Campaigns",
                type: "uuid",
                nullable: true);

            // Until now there was one YouTube sign-in, so every YouTube upload so far belongs to it, and a
            // campaign that has posted through it keeps it once a second page is added.
            migrationBuilder.Sql("""
                UPDATE clipper."PublishJobs" AS job
                SET "ConnectionId" = (
                    SELECT page."Id" FROM clipper."PlatformConnections" AS page
                    WHERE page."Platform" = 'YouTubeShorts' ORDER BY page."CreatedAt" LIMIT 1)
                WHERE job."Platform" = 'YouTubeShorts' AND job."DryRun" = FALSE;
                """);
            migrationBuilder.Sql("""
                UPDATE clipper."Campaigns" AS campaign
                SET "YouTubeConnectionId" = job."ConnectionId"
                FROM clipper."PublishJobs" AS job
                WHERE job."CampaignId" = campaign."Id" AND job."Platform" = 'YouTubeShorts' AND job."ConnectionId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_ConnectionId_ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs",
                columns: new[] { "ConnectionId", "ScheduledPublishAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_Platform",
                schema: "clipper",
                table: "PlatformConnections",
                column: "Platform");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublishJobs_ConnectionId_ScheduledPublishAt",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropIndex(
                name: "IX_PlatformConnections_Platform",
                schema: "clipper",
                table: "PlatformConnections");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                schema: "clipper",
                table: "PublishJobs");

            migrationBuilder.DropColumn(
                name: "YouTubeConnectionId",
                schema: "clipper",
                table: "Campaigns");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_Platform",
                schema: "clipper",
                table: "PlatformConnections",
                column: "Platform",
                unique: true);
        }
    }
}
