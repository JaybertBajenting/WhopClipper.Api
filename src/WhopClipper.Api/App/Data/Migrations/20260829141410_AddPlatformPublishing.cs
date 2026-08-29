using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformAccountKits",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAccountKits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformAccountKits_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformConnections",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    CredentialReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConfiguredFieldsJson = table.Column<string>(type: "text", nullable: false),
                    SecretFlagsJson = table.Column<string>(type: "text", nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "text", nullable: false),
                    AccountIdentifier = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishJobs",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    OutputRelativePath = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Caption = table.Column<string>(type: "text", nullable: false),
                    Privacy = table.Column<string>(type: "text", nullable: false),
                    ShareToFeed = table.Column<bool>(type: "boolean", nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    AccountIdentifier = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RemoteContainerId = table.Column<string>(type: "text", nullable: true),
                    FinalPublishIdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FinalPublishConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    ExternalUrl = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ManualPackageRelativePath = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishJobs_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAccountKits_CampaignId_Platform",
                schema: "clipper",
                table: "PlatformAccountKits",
                columns: new[] { "CampaignId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnections_Platform",
                schema: "clipper",
                table: "PlatformConnections",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_CampaignId",
                schema: "clipper",
                table: "PublishJobs",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_FinalPublishIdempotencyKey",
                schema: "clipper",
                table: "PublishJobs",
                column: "FinalPublishIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_IdempotencyKey",
                schema: "clipper",
                table: "PublishJobs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishJobs_Status_CreatedAt",
                schema: "clipper",
                table: "PublishJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAccountKits",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "PlatformConnections",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "PublishJobs",
                schema: "clipper");
        }
    }
}
