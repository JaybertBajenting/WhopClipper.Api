using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSubtitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubtitleRelativePath",
                schema: "clipper",
                table: "SourceAssets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitleRelativePath",
                schema: "clipper",
                table: "SourceAssets");
        }
    }
}
