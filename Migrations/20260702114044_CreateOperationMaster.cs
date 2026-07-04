using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FACTORY_MANAGEMENT_SYSTEM.Migrations
{
    /// <inheritdoc />
    public partial class CreateOperationMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationMasters",
                columns: table => new
                {
                    OperationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredGrade = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SMV = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationMasters", x => x.OperationId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationMasters");
        }
    }
}
