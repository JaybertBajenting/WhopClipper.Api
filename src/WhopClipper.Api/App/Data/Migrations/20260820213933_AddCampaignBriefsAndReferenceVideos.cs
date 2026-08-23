using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignBriefsAndReferenceVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignDocuments",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    ExtractedText = table.Column<string>(type: "text", nullable: false),
                    ExtractedCharacterCount = table.Column<int>(type: "integer", nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: true),
                    ExtractionStatus = table.Column<string>(type: "text", nullable: false),
                    ExtractionError = table.Column<string>(type: "text", nullable: true),
                    IncludeInExtraction = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignDocuments_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceVideos",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    LocalRelativePath = table.Column<string>(type: "text", nullable: true),
                    SubtitleRelativePath = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StyleProfileJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceVideos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceVideos_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignDocuments_CampaignId_ContentHash",
                schema: "clipper",
                table: "CampaignDocuments",
                columns: new[] { "CampaignId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceVideos_CampaignId_IsPrimary",
                schema: "clipper",
                table: "ReferenceVideos",
                columns: new[] { "CampaignId", "IsPrimary" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignDocuments",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "ReferenceVideos",
                schema: "clipper");
        }
    }
}
