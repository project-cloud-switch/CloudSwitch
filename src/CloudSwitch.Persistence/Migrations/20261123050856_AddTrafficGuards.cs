using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudSwitch.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrafficGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrafficGuards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ZoneReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RecordName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedRecord = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActiveJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClearedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClearedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Evidence = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficGuards", x => x.Id);
                    table.CheckConstraint("CK_TrafficGuards_State", "\"State\" IN ('Active', 'Uncertain', 'Cleared')");
                    table.ForeignKey(
                        name: "FK_TrafficGuards_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrafficGuards_Jobs_ActiveJobId",
                        column: x => x.ActiveJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrafficGuards_ActiveJobId",
                table: "TrafficGuards",
                column: "ActiveJobId");

            migrationBuilder.CreateIndex(
                name: "IX_TrafficGuards_Deployment",
                table: "TrafficGuards",
                column: "DeploymentId",
                filter: "\"State\" IN ('Active', 'Uncertain')");

            migrationBuilder.CreateIndex(
                name: "UX_TrafficGuards_ActiveRecord",
                table: "TrafficGuards",
                column: "NormalizedRecord",
                unique: true,
                filter: "\"State\" IN ('Active', 'Uncertain')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrafficGuards");
        }
    }
}
