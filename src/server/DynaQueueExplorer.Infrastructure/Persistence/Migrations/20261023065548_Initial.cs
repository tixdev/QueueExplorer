using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynaQueueExplorer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dqe");

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    User = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Cluster = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Vhost = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Resource = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "dqe",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleGrants",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ClusterPattern = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VhostPattern = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    QueuePattern = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleGrants_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "dqe",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TimestampUtc",
                schema: "dqe",
                table: "AuditEntries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_User_TimestampUtc",
                schema: "dqe",
                table: "AuditEntries",
                columns: new[] { "User", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                schema: "dqe",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_SubjectType_Subject_RoleId",
                schema: "dqe",
                table: "RoleAssignments",
                columns: new[] { "SubjectType", "Subject", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleGrants_RoleId",
                schema: "dqe",
                table: "RoleGrants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                schema: "dqe",
                table: "Roles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries",
                schema: "dqe");

            migrationBuilder.DropTable(
                name: "RoleAssignments",
                schema: "dqe");

            migrationBuilder.DropTable(
                name: "RoleGrants",
                schema: "dqe");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "dqe");
        }
    }
}
