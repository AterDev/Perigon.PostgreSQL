using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Migrations;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task MigrateAppliesInOrderIsRepeatableConcurrentAndCanRollback()
    {
        var schema = fixture.CreateIsolatedSchema("migrations");
        var connectionString = ForSchema(schema);
        await using var first = new MigrationTestDbContext(connectionString, schema);
        await using var second = new MigrationTestDbContext(connectionString, schema);

        await Task.WhenAll(first.MigrateAsync(), second.MigrateAsync());
        await first.MigrateAsync();

        Assert.True(await TableExistsAsync(connectionString, "migration_items"));
        Assert.True(await ColumnExistsAsync(connectionString, "migration_items", "description"));
        Assert.Equal(2L, await ScalarLongAsync(connectionString, "SELECT count(*) FROM \"_History\""));

        await first.MigrateAsync("1000_initial");

        Assert.False(await ColumnExistsAsync(connectionString, "migration_items", "description"));
        Assert.Equal(1L, await ScalarLongAsync(connectionString, "SELECT count(*) FROM \"_History\""));
    }

    [Fact]
    public async Task FailedMigrationRollsBackDdlAndDoesNotWriteHistory()
    {
        var schema = fixture.CreateIsolatedSchema("migration_failure");
        var connectionString = ForSchema(schema);
        await using var db = new FailingMigrationTestDbContext(connectionString, schema);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.MigrateAsync());

        Assert.Equal("22012", exception.SqlState);
        Assert.Equal(1L, await ScalarLongAsync(connectionString, "SELECT count(*) FROM \"_History\""));
        Assert.True(await TableExistsAsync(connectionString, "failing_migration_marker"));
    }

    [Fact]
    public async Task BaselineValidatesManagedSchemaBeforeRecordingHistory()
    {
        var schema = fixture.CreateIsolatedSchema("migration_baseline");
        var connectionString = ForSchema(schema);
        await using var db = new BaselineMigrationTestDbContext(connectionString, schema);
        await db.EnsureCreatedAsync();
        var expectedModel = db.GetMigrationModel();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("ALTER TABLE baseline_entities ADD COLUMN unexpected text", connection);
            await command.ExecuteNonQueryAsync();
        }

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.BaselineAsync("1000_baseline", expectedModel));
        Assert.Contains("does not match", mismatch.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(connectionString, "_History"));

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("ALTER TABLE baseline_entities DROP COLUMN unexpected", connection);
            await command.ExecuteNonQueryAsync();
        }

        await db.BaselineAsync("1000_baseline", expectedModel);

        Assert.Equal(1L, await ScalarLongAsync(connectionString, "SELECT count(*) FROM \"_History\""));
        Assert.Equal("1000_baseline", await ScalarStringAsync(connectionString, "SELECT \"MigrationId\" FROM \"_History\""));
    }

    private string ForSchema(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };
        return builder.ConnectionString;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
        command.Parameters.AddWithValue(table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = $1 AND column_name = $2)", connection);
        command.Parameters.AddWithValue(table);
        command.Parameters.AddWithValue(column);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarLongAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}

public sealed class MigrationTestDbContext(string connectionString, string historySchema)
    : DbContext(builder => builder.UsePostgres(connectionString).UseMigrationsHistoryTable("_History", historySchema));

[Migration(typeof(MigrationTestDbContext), "1000_initial")]
public sealed class MigrationTestInitial : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE migration_items (id integer PRIMARY KEY)");

    public override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE migration_items");
}

[Migration(typeof(MigrationTestDbContext), "2000_add_description")]
public sealed class MigrationTestAddDescription : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE migration_items ADD COLUMN description text NULL");

    public override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE migration_items DROP COLUMN description");
}

public sealed class FailingMigrationTestDbContext(string connectionString, string historySchema)
    : DbContext(builder => builder.UsePostgres(connectionString).UseMigrationsHistoryTable("_History", historySchema));

[Migration(typeof(FailingMigrationTestDbContext), "1000_initial")]
public sealed class FailingMigrationTestInitial : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TABLE failing_migration_marker (id integer PRIMARY KEY)");

    public override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE failing_migration_marker");
}

[Migration(typeof(FailingMigrationTestDbContext), "2000_failure")]
public sealed class FailingMigrationTestFailure : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1 / 0");

    public override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
}

public sealed class BaselineMigrationTestDbContext(string connectionString, string historySchema)
    : DbContext(builder => builder.UsePostgres(connectionString).UseMigrationsHistoryTable("_History", historySchema))
{
    public DbSet<BaselineMigrationEntity> Entities => Set<BaselineMigrationEntity>();
}

[Table("baseline_entities")]
public sealed class BaselineMigrationEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string? Value { get; set; }
}

[Migration(typeof(BaselineMigrationTestDbContext), "1000_baseline")]
public sealed class BaselineMigration : Migration
{
    public override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("SELECT 1");

    public override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("SELECT 1");
}
