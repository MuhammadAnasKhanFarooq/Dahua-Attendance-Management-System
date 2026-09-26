using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using DahuaAttendanceAPI.Data;

#nullable disable

namespace DahuaAttendanceAPI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260827_AddAttendanceUserFaces")]
    partial class AddAttendanceUserFaces
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.10")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            modelBuilder.Entity("DahuaAttendanceAPI.Models.AttendanceUserEntity", b =>
            {
                b.Property<int>("Id").ValueGeneratedOnAdd();
                b.Property<string>("UserId").IsRequired().HasMaxLength(100);
                b.Property<string>("UserName").HasMaxLength(200);
                b.Property<string>("CardNo").HasMaxLength(100);
                b.Property<string>("Password").HasMaxLength(200);
                b.Property<string>("ClassNumber").HasMaxLength(100);
                b.Property<string>("PhoneNumber").HasMaxLength(50);
                b.Property<int>("PhotoLength");
                b.Property<string>("EnrollmentStatus").HasMaxLength(50);
                b.Property<string>("PhotoFileName").HasMaxLength(260);
                b.Property<DateTime>("CreatedAt");
                b.Property<DateTime?>("UpdatedAt");
                b.HasKey("Id");
                b.ToTable("AttendanceUsers");
            });

            modelBuilder.Entity("DahuaAttendanceAPI.Models.AttendanceUserFaceEntity", b =>
            {
                b.Property<int>("Id").ValueGeneratedOnAdd();
                b.Property<int>("AttendanceUserId");
                b.Property<string>("UserId").HasMaxLength(100);
                b.Property<int>("FaceIndex");
                b.Property<string>("PhotoFileName").HasMaxLength(260);
                b.Property<int>("PhotoLength");
                b.Property<DateTime>("CreatedAt");
                b.Property<DateTime?>("UpdatedAt");
                b.Property<bool>("IsActive");

                b.HasKey("Id");

                b.HasIndex("AttendanceUserId").HasDatabaseName("IX_AttendanceUserFaces_AttendanceUserId");
                b.HasIndex(new[] { "AttendanceUserId", "FaceIndex" }).IsUnique().HasDatabaseName("UX_AttendanceUserFaces_User_FaceIndex");

                b.HasOne("DahuaAttendanceAPI.Models.AttendanceUserEntity")
                    .WithMany()
                    .HasForeignKey("AttendanceUserId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();

                b.ToTable("AttendanceUserFaces");
            });

            modelBuilder.Entity("DahuaAttendanceAPI.Models.AttendanceLogEntity", b =>
            {
                b.Property<int>("Id").ValueGeneratedOnAdd();
                b.Property<string>("UserId").IsRequired().HasMaxLength(100);
                b.Property<string>("UserName").HasMaxLength(200);
                b.Property<DateTime>("EventTime");
                b.Property<int>("Confidence");
                b.Property<int>("ChannelId");
                b.Property<int>("EventId");
                b.Property<string>("Source").IsRequired().HasMaxLength(20).HasDefaultValue("DAHUA");
                b.Property<DateTime>("RecordedAt");
                b.HasKey("Id");
                b.ToTable("AttendanceLogs");
            });
        }
    }
}
