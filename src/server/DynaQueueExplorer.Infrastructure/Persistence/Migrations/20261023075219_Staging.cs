using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynaQueueExplorer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Staging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagingBatches",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    OriginCluster = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OriginVhost = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OriginQueue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagingBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StagedMessages",
                schema: "dqe",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RoutingKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PropertiesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Body = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Discarded = table.Column<bool>(type: "bit", nullable: false),
                    LastPublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPublishedTo = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StagedMessages_StagingBatches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "dqe",
                        principalTable: "StagingBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StagedMessages_BatchId_Position",
                schema: "dqe",
                table: "StagedMessages",
                columns: new[] { "BatchId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagedMessages",
                schema: "dqe");

            migrationBuilder.DropTable(
                name: "StagingBatches",
                schema: "dqe");
        }
    }
}
