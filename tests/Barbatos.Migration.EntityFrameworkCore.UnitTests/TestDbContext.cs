// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Barbatos.Migration.EntityFrameworkCore.UnitTests;

public sealed class User
{
    public int Id { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.FullName).IsRequired();
        });
    }
}

/// <summary>
/// Hand-written rather than scaffolded: the design-time tooling is not available in a unit test,
/// and a migration class is only what the tooling would have generated anyway.
/// </summary>
[Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(TestDbContext))]
[Microsoft.EntityFrameworkCore.Migrations.Migration("20260101000000_CreateUsers")]
public sealed class CreateUsers : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
                FullName = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Users", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("Users");
}

[Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(TestDbContext))]
[Microsoft.EntityFrameworkCore.Migrations.Migration("20260201000000_SplitName")]
public sealed class SplitName : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FirstName", "Users", nullable: true);
        migrationBuilder.AddColumn<string>("LastName", "Users", nullable: true);
    }

    // Raw SQL rather than DropColumn: EF Core's SQLite provider implements a column drop by
    // rebuilding the table, which needs the model snapshot the design-time tooling would have
    // generated. SQLite has supported ALTER TABLE ... DROP COLUMN natively since 3.35, and
    // hand-written SQL in a migration is ordinary practice anyway.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Users DROP COLUMN FirstName;");
        migrationBuilder.Sql("ALTER TABLE Users DROP COLUMN LastName;");
    }
}
