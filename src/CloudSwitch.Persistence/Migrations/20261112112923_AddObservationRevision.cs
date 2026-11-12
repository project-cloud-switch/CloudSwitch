using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudSwitch.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddObservationRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ObservationRevision",
                table: "Deployments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
            
            migrationBuilder.Sql("UPDATE \"Deployments\" SET \"ObservationRevision\" = \"Revision\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObservationRevision",
                table: "Deployments");
        }
    }
}
