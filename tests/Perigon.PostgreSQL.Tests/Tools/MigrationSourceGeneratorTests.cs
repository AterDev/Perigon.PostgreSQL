using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Migrations;

namespace Perigon.PostgreSQL.Tests.Tools;

public sealed class MigrationSourceGeneratorTests
{
    [Fact]
    public void GeneratesMigrationClassWithContextIdUpDownAndWarnings()
    {
        var diff = new MigrationDiff(
            [new MigrationCommand("ALTER TABLE \"users\" DROP COLUMN \"name\"", true, "Column data will be lost.")],
            [new MigrationCommand("ALTER TABLE \"users\" ADD COLUMN \"name\" text")],
            ["Column data will be lost."]);

        var source = MigrationSourceGenerator.GenerateMigration(
            "Demo.Migrations",
            typeof(MigrationCliTestContext),
            "202610190101_RemoveName",
            "RemoveNameMigration",
            diff);

        Assert.Contains("[Migration(typeof(Perigon.PostgreSQL.Tests.Tools.MigrationCliTestContext), \"202610190101_RemoveName\")]", source);
        Assert.Contains("DROP COLUMN \\u0022name\\u0022", source);
        Assert.Contains("public override void Down(MigrationBuilder migrationBuilder)", source);
        Assert.Contains("ADD COLUMN", source);
    }

    [Fact]
    public void GeneratesStableSnapshotWithSchemaAndConstraints()
    {
        var model = new MigrationModel(
        [
            new MigrationTable(
                "app",
                "users",
                "users table",
                [new MigrationColumn("id", "integer", false, true, false, null, null, "primary id")],
                new MigrationPrimaryKey("pk_users", ["id"]),
                [],
                [])
        ]);

        var source = MigrationSourceGenerator.GenerateSnapshot("Demo.Migrations", typeof(MigrationCliTestContext), model);

        Assert.Contains("[MigrationSnapshot(typeof(Perigon.PostgreSQL.Tests.Tools.MigrationCliTestContext))]", source);
        Assert.Contains("new MigrationTable(\"app\", \"users\", \"users table\"", source);
        Assert.Contains("new MigrationPrimaryKey(\"pk_users\", new string[] { \"id\" })", source);
    }

    [Theory]
    [InlineData("migrations", "add")]
    [InlineData("migrations", "list")]
    [InlineData("migrations", "script")]
    [InlineData("database", "update")]
    [InlineData("database", "baseline")]
    public void RecognizesMigrationCommands(string command, string action)
    {
        Assert.True(MigrationCli.IsMigrationCommand([command, action]));
    }

    [Fact]
    public void ScaffoldCommandsStayOnExistingCommandPath()
    {
        Assert.False(MigrationCli.IsMigrationCommand(["database", "scaffold"]));
    }
}

public sealed class MigrationCliTestContext : DbContext
{
}

[Migration(typeof(MigrationCliTestContext), "0001_cli_fixture")]
public sealed class MigrationCliFixtureMigration : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE \"cli_fixture\" (id integer PRIMARY KEY)");

    public override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE \"cli_fixture\"");
}
