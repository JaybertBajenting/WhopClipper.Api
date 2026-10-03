using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAssetSegment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SegmentEndSeconds",
                schema: "clipper",
                table: "MediaLibraryAssets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SegmentStartSeconds",
                schema: "clipper",
                table: "MediaLibraryAssets",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SegmentEndSeconds",
                schema: "clipper",
                table: "MediaLibraryAssets");

            migrationBuilder.DropColumn(
                name: "SegmentStartSeconds",
                schema: "clipper",
                table: "MediaLibraryAssets");
        }
    }
}
