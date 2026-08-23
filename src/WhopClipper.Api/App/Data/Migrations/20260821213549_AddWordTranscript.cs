using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWordTranscript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WordTranscriptRelativePath",
                schema: "clipper",
                table: "SourceAssets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WordTranscriptRelativePath",
                schema: "clipper",
                table: "SourceAssets");
        }
    }
}
