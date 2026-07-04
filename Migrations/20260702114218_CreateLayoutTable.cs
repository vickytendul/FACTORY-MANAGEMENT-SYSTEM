using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FACTORY_MANAGEMENT_SYSTEM.Migrations
{
    /// <inheritdoc />
    public partial class CreateLayoutTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Layouts",
                columns: table => new
                {
                    LayoutId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CCId = table.Column<int>(type: "int", nullable: false),
                    OperationId = table.Column<int>(type: "int", nullable: false),
                    OperationMasterOperationId = table.Column<int>(type: "int", nullable: true),
                    OperationSequence = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Layouts", x => x.LayoutId);
                    table.ForeignKey(
                        name: "FK_Layouts_CCs_CCId",
                        column: x => x.CCId,
                        principalTable: "CCs",
                        principalColumn: "CCId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Layouts_OperationMasters_OperationMasterOperationId",
                        column: x => x.OperationMasterOperationId,
                        principalTable: "OperationMasters",
                        principalColumn: "OperationId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Layouts_CCId",
                table: "Layouts",
                column: "CCId");

            migrationBuilder.CreateIndex(
                name: "IX_Layouts_OperationMasterOperationId",
                table: "Layouts",
                column: "OperationMasterOperationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Layouts");
        }
    }
}
