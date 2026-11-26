using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smartwarranty_2.Migrations
{
    /// <inheritdoc />
    public partial class AddWarrantyCoverageCategoriesAndInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Warranties_ProductId",
                table: "Warranties");

            migrationBuilder.RenameColumn(
                name: "WarrantyInformationStatus",
                table: "Products",
                newName: "ManufacturerWarrantyStatus");

            migrationBuilder.AddColumn<int>(
                name: "CoverageCategory",
                table: "Warranties",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "WITH RankedWarranties AS (" +
                "SELECT Id, ROW_NUMBER() OVER (PARTITION BY ProductId ORDER BY CreatedAt, Id) AS CoverageOrder " +
                "FROM Warranties) " +
                "UPDATE Warranties SET CoverageCategory = CASE WHEN RankedWarranties.CoverageOrder = 1 THEN 0 ELSE 1 END " +
                "FROM Warranties INNER JOIN RankedWarranties ON Warranties.Id = RankedWarranties.Id");

            migrationBuilder.AddColumn<int>(
                name: "WarrantyId",
                table: "ProductDocuments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warranties_ProductId",
                table: "Warranties",
                column: "ProductId",
                unique: true,
                filter: "[CoverageCategory] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDocuments_WarrantyId",
                table: "ProductDocuments",
                column: "WarrantyId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductDocuments_Warranties_WarrantyId",
                table: "ProductDocuments",
                column: "WarrantyId",
                principalTable: "Warranties",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductDocuments_Warranties_WarrantyId",
                table: "ProductDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Warranties_ProductId",
                table: "Warranties");

            migrationBuilder.DropIndex(
                name: "IX_ProductDocuments_WarrantyId",
                table: "ProductDocuments");

            migrationBuilder.DropColumn(
                name: "CoverageCategory",
                table: "Warranties");

            migrationBuilder.DropColumn(
                name: "WarrantyId",
                table: "ProductDocuments");

            migrationBuilder.RenameColumn(
                name: "ManufacturerWarrantyStatus",
                table: "Products",
                newName: "WarrantyInformationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Warranties_ProductId",
                table: "Warranties",
                column: "ProductId");
        }
    }
}
