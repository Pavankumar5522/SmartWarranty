using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smartwarranty_2.Migrations
{
    /// <inheritdoc />
    public partial class AddProductWarrantyInformationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WarrantyInformationStatus",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE p SET WarrantyInformationStatus = 1 " +
                "FROM Products p " +
                "WHERE EXISTS (SELECT 1 FROM Warranties w WHERE w.ProductId = p.Id)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarrantyInformationStatus",
                table: "Products");
        }
    }
}
