using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class EnsureCreatedMetadataIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public EnsureCreatedMetadataIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreated_applies_attribute_metadata_to_real_postgres_objects()
    {
        const string schema = "ensure_created_attribute_meta";
        await ResetSchemaAsync(schema);

        await using var db = new AttributeMetadataDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var amount = await GetColumnAsync(schema, "attribute_users", "amount");
        Assert.Equal("numeric", amount.DataType);
        Assert.Equal(12, amount.NumericPrecision);
        Assert.Equal(4, amount.NumericScale);

        var createdAt = await GetColumnAsync(schema, "attribute_users", "created_at");
        Assert.Equal("timestamp with time zone", createdAt.DataType);
        Assert.Equal(3, createdAt.DateTimePrecision);

        var email = await GetColumnAsync(schema, "attribute_users", "email");
        Assert.Equal("text", email.DataType);
        Assert.Equal("NO", email.IsNullable);

        var code = await GetColumnAsync(schema, "attribute_users", "code");
        Assert.Equal("text", code.DataType);

        Assert.Equal("attribute table comment", await GetTableCommentAsync(schema, "attribute_users"));
        Assert.Equal("attribute email comment", await GetColumnCommentAsync(schema, "attribute_users", "email"));
        Assert.True(await IndexExistsAsync(schema, "uq_attribute_users_email", isUnique: true));
    }

    [Fact]
    public async Task EnsureCreated_applies_fluent_metadata_to_real_postgres_objects()
    {
        const string schema = "ensure_created_fluent_meta";
        await ResetSchemaAsync(schema);

        await using var db = new FluentMetadataDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var amount = await GetColumnAsync(schema, "fluent_users", "amount");
        Assert.Equal("numeric", amount.DataType);
        Assert.Equal(10, amount.NumericPrecision);
        Assert.Equal(2, amount.NumericScale);

        var createdAt = await GetColumnAsync(schema, "fluent_users", "created_at");
        Assert.Equal("timestamp with time zone", createdAt.DataType);
        Assert.Equal(2, createdAt.DateTimePrecision);

        var email = await GetColumnAsync(schema, "fluent_users", "contact_email");
        Assert.Equal("text", email.DataType);
        Assert.Equal("NO", email.IsNullable);

        var code = await GetColumnAsync(schema, "fluent_users", "code");
        Assert.Equal("text", code.DataType);

        Assert.Equal("fluent table comment", await GetTableCommentAsync(schema, "fluent_users"));
        Assert.Equal("fluent email comment", await GetColumnCommentAsync(schema, "fluent_users", "contact_email"));
        Assert.True(await IndexExistsAsync(schema, "uq_fluent_users_contact_email", isUnique: true));
    }

    private async Task ResetSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ColumnInfo> GetColumnAsync(string schema, string table, string column)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data_type, numeric_precision, numeric_scale, datetime_precision, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table AND column_name = @column
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ColumnInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetString(4));
    }

    private async Task<string?> GetTableCommentAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT obj_description(c.oid)
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<string?> GetColumnCommentAsync(string schema, string table, string column)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT col_description(c.oid, a.attnum)
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            INNER JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = @schema AND c.relname = @table AND a.attname = @column AND a.attnum > 0 AND NOT a.attisdropped
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<bool> IndexExistsAsync(string schema, string indexName, bool isUnique)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.indisunique
            FROM pg_class idx
            INNER JOIN pg_namespace n ON n.oid = idx.relnamespace
            INNER JOIN pg_index i ON i.indexrelid = idx.oid
            WHERE n.nspname = @schema AND idx.relname = @indexName
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("indexName", indexName);
        var value = await command.ExecuteScalarAsync();
        return value is bool actual && actual == isUnique;
    }

    private sealed record ColumnInfo(string DataType, int? NumericPrecision, int? NumericScale, int? DateTimePrecision, string IsNullable);
}

[Comment("attribute table comment")]
[System.ComponentModel.DataAnnotations.Schema.Table("attribute_users", Schema = "ensure_created_attribute_meta")]
[Index(nameof(Email), Name = "uq_attribute_users_email", IsUnique = true)]
public sealed class AttributeMetadataUser
{
    public int Id { get; set; }

    [Required]
    [Comment("attribute email comment")]
    public string Email { get; set; } = "";

    [Precision(12, 4)]
    public decimal Amount { get; set; }

    [Precision(3)]
    public DateTime CreatedAt { get; set; }

    [Unicode(false)]
    public string Code { get; set; } = "";
}

public sealed class AttributeMetadataDbContext : DbContext
{
    public AttributeMetadataDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<AttributeMetadataUser> AttributeUsers => Set<AttributeMetadataUser>();
}

public sealed class FluentMetadataUser
{
    public int Id { get; set; }

    public string Email { get; set; } = "";

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Code { get; set; } = "";
}

public sealed class FluentMetadataDbContext : DbContext
{
    public FluentMetadataDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<FluentMetadataUser> FluentUsers => Set<FluentMetadataUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FluentMetadataUser>(entity =>
        {
            entity.ToTable("fluent_users", "ensure_created_fluent_meta");
            entity.HasComment("fluent table comment");
            entity.Property(user => user.Email)
                .HasColumnName("contact_email")
                .IsRequired()
                .HasComment("fluent email comment");
            entity.Property(user => user.Amount).HasPrecision(10, 2);
            entity.Property(user => user.CreatedAt).HasPrecision(2);
            entity.Property(user => user.Code).IsUnicode(false);
            entity.HasIndex(user => user.Email)
                .HasDatabaseName("uq_fluent_users_contact_email")
                .IsUnique();
        });
    }
}