using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Migrations;
using Perigon.PostgreSQL.Options;
using System.Reflection;

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
        Assert.Contains("            ALTER TABLE \"users\" DROP COLUMN \"name\"", source);
        Assert.Contains("\"\"\"" + Environment.NewLine, source);
        Assert.Contains("public override void Down(MigrationBuilder migrationBuilder)", source);
        Assert.Contains("ADD COLUMN", source);
    }

    [Theory]
    [InlineData("CREATE TABLE \"users\" (\n    \"id\" integer\n)")]
    [InlineData("SELECT '\"\"\"', '\\path';\r\n\n  \r-- 注释\n")]
    [InlineData("")]
    public void GeneratesRawSqlLiteralThatPreservesCommandText(string sql)
    {
        var diff = new MigrationDiff([new MigrationCommand(sql)], [], []);
        var source = MigrationSourceGenerator.GenerateMigration(
            "Demo.Migrations",
            typeof(MigrationCliTestContext),
            "0001_Test",
            "TestMigration",
            diff);

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        Assert.Empty(syntaxTree.GetDiagnostics());
        var invocation = Assert.Single(syntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>());
        var literal = Assert.IsType<LiteralExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
        Assert.Equal(SyntaxKind.MultiLineRawStringLiteralToken, literal.Token.Kind());
        Assert.Equal(sql, literal.Token.ValueText);
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

    [Fact]
    public async Task CreatesOptionsOnlyContextWithReflectedOptionsBuilder()
    {
        var createContext = typeof(ProjectContext).GetMethod("CreateContext", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createContext);

        var context = Assert.IsType<OptionsOnlyMigrationCliContext>(createContext!.Invoke(
            null,
            [typeof(OptionsOnlyMigrationCliContext), "Host=localhost;Database=cli_options;Username=test;Password=test"]));
        await using (context)
        {
            var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(context.GetDataSource().ConnectionString);
            Assert.Equal("localhost", connectionString.Host);
            Assert.Equal("cli_options", connectionString.Database);
        }
    }
}

public sealed class MigrationCliTestContext : DbContext
{
}

public sealed class OptionsOnlyMigrationCliContext(DbContextOptions<OptionsOnlyMigrationCliContext> options)
    : DbContext(options)
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
