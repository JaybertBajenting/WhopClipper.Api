using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhopClipper.Api.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAssetEnergy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Energy",
                schema: "clipper",
                table: "MediaLibraryAssets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Energy",
                schema: "clipper",
                table: "MediaLibraryAssets");
        }
    }
}
