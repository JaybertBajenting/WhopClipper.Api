using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialClipper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "clipper");

            migrationBuilder.CreateTable(
                name: "AiAnalysisRuns",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    ProvidersJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RawOutputJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    PromptVersion = table.Column<string>(type: "text", nullable: false),
                    SchemaVersion = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAnalysisRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                schema: "clipper",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    WhopUrl = table.Column<string>(type: "text", nullable: true),
                    CampaignType = table.Column<string>(type: "text", nullable: false),
                    Brand = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    PayoutAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    PayoutUnit = table.Column<string>(type: "text", nullable: true),
                    Deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RawInstructions = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EditPlans",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    FramesPerSecond = table.Column<int>(type: "integer", nullable: false),
                    Crf = table.Column<int>(type: "integer", nullable: false),
                    NormalizeAudio = table.Column<bool>(type: "boolean", nullable: false),
                    BurnSubtitles = table.Column<bool>(type: "boolean", nullable: false),
                    SubtitleRelativePath = table.Column<string>(type: "text", nullable: true),
                    MusicAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    MusicVolume = table.Column<double>(type: "double precision", nullable: false),
                    OutputFileName = table.Column<string>(type: "text", nullable: false),
                    ExtraSettingsJson = table.Column<string>(type: "text", nullable: true),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    LastFfmpegArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    StandardOutput = table.Column<string>(type: "text", nullable: false),
                    StandardError = table.Column<string>(type: "text", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaLibraryLocations",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaLibraryLocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HighlightCandidates",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AiAnalysisRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Hook = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    EditPlanJson = table.Column<string>(type: "text", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HighlightCandidates_AiAnalysisRuns_AiAnalysisRunId",
                        column: x => x.AiAnalysisRunId,
                        principalSchema: "clipper",
                        principalTable: "AiAnalysisRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlatformProfiles",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    MinDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    PreferredDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    MaxDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CaptionTemplate = table.Column<string>(type: "text", nullable: false),
                    RequiredHashtagsJson = table.Column<string>(type: "text", nullable: false),
                    RequiredMentionsJson = table.Column<string>(type: "text", nullable: false),
                    DisclosureText = table.Column<string>(type: "text", nullable: true),
                    CallToAction = table.Column<string>(type: "text", nullable: true),
                    BurnSubtitles = table.Column<bool>(type: "boolean", nullable: false),
                    SafeAreaNotes = table.Column<string>(type: "text", nullable: false),
                    ComplianceChecklistJson = table.Column<string>(type: "text", nullable: false),
                    ValueOriginsJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformProfiles_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequirementRevisions",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RawInstructions = table.Column<string>(type: "text", nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequirementRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequirementRevisions_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceAssets",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Uploader = table.Column<string>(type: "text", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "text", nullable: true),
                    LocalRelativePath = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DownloadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceAssets_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "clipper",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaLibraryAssets",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullPath = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    TagsJson = table.Column<string>(type: "text", nullable: false),
                    Mood = table.Column<string>(type: "text", nullable: true),
                    Genre = table.Column<string>(type: "text", nullable: true),
                    Bpm = table.Column<double>(type: "double precision", nullable: true),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    LicenseNotes = table.Column<string>(type: "text", nullable: true),
                    IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaLibraryAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaLibraryAssets_MediaLibraryLocations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "clipper",
                        principalTable: "MediaLibraryLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequirementItems",
                schema: "clipper",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequirementItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequirementItems_RequirementRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "clipper",
                        principalTable: "RequirementRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Status",
                schema: "clipper",
                table: "Campaigns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_HighlightCandidates_AiAnalysisRunId",
                schema: "clipper",
                table: "HighlightCandidates",
                column: "AiAnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_CreatedAt",
                schema: "clipper",
                table: "Jobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaLibraryAssets_LocationId_FullPath",
                schema: "clipper",
                table: "MediaLibraryAssets",
                columns: new[] { "LocationId", "FullPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformProfiles_CampaignId_Platform",
                schema: "clipper",
                table: "PlatformProfiles",
                columns: new[] { "CampaignId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequirementItems_RevisionId",
                schema: "clipper",
                table: "RequirementItems",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_RequirementRevisions_CampaignId_Version",
                schema: "clipper",
                table: "RequirementRevisions",
                columns: new[] { "CampaignId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceAssets_CampaignId",
                schema: "clipper",
                table: "SourceAssets",
                column: "CampaignId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationSettings",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "EditPlans",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "HighlightCandidates",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "MediaLibraryAssets",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "PlatformProfiles",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "RequirementItems",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "SourceAssets",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "AiAnalysisRuns",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "MediaLibraryLocations",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "RequirementRevisions",
                schema: "clipper");

            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "clipper");
        }
    }
}
