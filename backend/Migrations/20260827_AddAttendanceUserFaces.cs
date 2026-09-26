using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DahuaAttendanceAPI.Migrations
{
    public partial class AddAttendanceUserFaces : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceUserFaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AttendanceUserId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FaceIndex = table.Column<int>(type: "int", nullable: false),
                    PhotoFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    PhotoLength = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceUserFaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceUserFaces_AttendanceUsers_AttendanceUserId",
                        column: x => x.AttendanceUserId,
                        principalTable: "AttendanceUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceUserFaces_AttendanceUserId",
                table: "AttendanceUserFaces",
                column: "AttendanceUserId");

            migrationBuilder.CreateIndex(
                name: "UX_AttendanceUserFaces_User_FaceIndex",
                table: "AttendanceUserFaces",
                columns: new[] { "AttendanceUserId", "FaceIndex" },
                unique: true);

            // Add check constraint to limit FaceIndex values to 1 or 2
            migrationBuilder.Sql("ALTER TABLE [AttendanceUserFaces] ADD CONSTRAINT CK_AttendanceUserFaces_FaceIndex CHECK ([FaceIndex] IN (1,2));");

            // Backfill existing AttendanceUsers photo into AttendanceUserFaces as FaceIndex = 1
            migrationBuilder.Sql(@"
                INSERT INTO AttendanceUserFaces
                    (AttendanceUserId, UserId, FaceIndex, PhotoFileName, PhotoLength, CreatedAt, UpdatedAt, IsActive)
                SELECT Id, UserId, 1, PhotoFileName, PhotoLength, CreatedAt, UpdatedAt, 1
                FROM AttendanceUsers
                WHERE PhotoFileName IS NOT NULL AND LTRIM(RTRIM(PhotoFileName)) <> ''
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceUserFaces");
        }
    }
}
