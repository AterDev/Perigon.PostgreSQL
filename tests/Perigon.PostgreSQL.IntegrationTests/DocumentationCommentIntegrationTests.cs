using Npgsql;
using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Migrations;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DocumentationCommentIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task EnsureCreated_writes_summary_and_explicit_comments_to_postgresql()
    {
        var schema = fixture.CreateIsolatedSchema("documentation_comments");
        await using var context = new DocumentationCommentDbContext(fixture.ConnectionString, schema);
        await context.EnsureCreatedAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT obj_description(c.oid, 'pg_class'), col_description(c.oid, a.attnum)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = $1 AND c.relname = 'DocumentedItems' AND a.attname = 'DisplayName'
            """, connection);
        command.Parameters.AddWithValue(schema);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Documented inventory items.", reader.GetString(0));
        Assert.Equal("Explicit display name.", reader.GetString(1));
    }

    [Theory]
    [InlineData(false, "DocumentedCategories", "DisplayName")]
    [InlineData(true, "documented_categories", "display_name")]
    public async Task Migration_ddl_and_queries_use_the_selected_naming_convention(
        bool snakeCase,
        string table,
        string column)
    {
        await using var context = new ConventionDocumentationDbContext(fixture.ConnectionString, snakeCase);
        var model = context.GetMigrationModel();
        Assert.Equal(table, Assert.Single(model.Tables).Name);
        var diff = MigrationModelDiffer.Diff(MigrationModel.Empty, model);
        Assert.Contains(diff.Up, command =>
            command.Sql == $"COMMENT ON TABLE \"{table}\" IS 'Documented inventory items.'");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        try
        {
            foreach (var migrationCommand in diff.Up)
            {
                await using var command = new NpgsqlCommand(migrationCommand.Sql, connection);
                await command.ExecuteNonQueryAsync();
            }

            await context.Items.InsertAsync(new DocumentedCategory { Id = 1, DisplayName = "Example" });
            Assert.Equal("Example", (await context.Items.ToListAsync()).Single().DisplayName);

            await using var comment = new NpgsqlCommand(
                """
                SELECT col_description(c.oid, a.attnum)
                FROM pg_class c
                JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE c.oid = to_regclass(quote_ident($1)) AND a.attname = $2
                """, connection);
            comment.Parameters.AddWithValue(table);
            comment.Parameters.AddWithValue(column);
            Assert.Equal("Explicit display name.", await comment.ExecuteScalarAsync());
        }
        finally
        {
            foreach (var migrationCommand in diff.Down)
            {
                await using var command = new NpgsqlCommand(migrationCommand.Sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}

/// <summary>Documented inventory items.</summary>
public sealed class DocumentedCategory
{
    public int Id { get; set; }

    /// <summary>Display name fallback.</summary>
    [Comment("Explicit display name.")]
    public string DisplayName { get; set; } = "";
}

public sealed class DocumentationCommentDbContext(string connectionString, string schema)
    : DbContext(options => options.UsePostgres(connectionString))
{
    public DbSet<DocumentedCategory> Items => Set<DocumentedCategory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentedCategory>().ToTable("DocumentedItems", schema);
        modelBuilder.Entity<DocumentedCategory>().Property(item => item.DisplayName).HasColumnName("DisplayName");
    }
}

public sealed class ConventionDocumentationDbContext(string connectionString, bool snakeCase)
    : DbContext(options =>
    {
        options.UsePostgres(connectionString).UseSnakeCaseNaming(snakeCase);
    })
{
    public DbSet<DocumentedCategory> Items => Set<DocumentedCategory>();
}
