using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynaQueueExplorer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StagingEditsAndBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Broker",
                schema: "dqe",
                table: "StagingBatches",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "rabbitmq");

            migrationBuilder.AddColumn<string>(
                name: "Broker",
                schema: "dqe",
                table: "StagedMessages",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "rabbitmq");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAt",
                schema: "dqe",
                table: "StagedMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditedBy",
                schema: "dqe",
                table: "StagedMessages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OriginalBody",
                schema: "dqe",
                table: "StagedMessages",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPropertiesJson",
                schema: "dqe",
                table: "StagedMessages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagingBatches_Broker",
                schema: "dqe",
                table: "StagingBatches",
                column: "Broker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StagingBatches_Broker",
                schema: "dqe",
                table: "StagingBatches");

            migrationBuilder.DropColumn(
                name: "Broker",
                schema: "dqe",
                table: "StagingBatches");

            migrationBuilder.DropColumn(
                name: "Broker",
                schema: "dqe",
                table: "StagedMessages");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                schema: "dqe",
                table: "StagedMessages");

            migrationBuilder.DropColumn(
                name: "EditedBy",
                schema: "dqe",
                table: "StagedMessages");

            migrationBuilder.DropColumn(
                name: "OriginalBody",
                schema: "dqe",
                table: "StagedMessages");

            migrationBuilder.DropColumn(
                name: "OriginalPropertiesJson",
                schema: "dqe",
                table: "StagedMessages");
        }
    }
}
