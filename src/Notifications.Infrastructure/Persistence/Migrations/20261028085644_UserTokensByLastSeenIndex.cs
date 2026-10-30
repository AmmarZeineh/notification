using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserTokensByLastSeenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceTokens_UserId",
                table: "DeviceTokens");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId_LastSeenAt",
                table: "DeviceTokens",
                columns: new[] { "UserId", "LastSeenAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Token" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceTokens_UserId_LastSeenAt",
                table: "DeviceTokens");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId",
                table: "DeviceTokens",
                column: "UserId");
        }
    }
}
