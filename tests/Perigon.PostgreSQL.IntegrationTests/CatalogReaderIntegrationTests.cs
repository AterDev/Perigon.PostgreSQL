using Npgsql;
using Perigon.PostgreSQL.Tools.ReverseEngineering;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogReaderIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public CatalogReaderIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Catalog_reader_reads_composite_relational_metadata_and_supported_index_shapes()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP SCHEMA IF EXISTS scaffold_review CASCADE;
                CREATE SCHEMA scaffold_review;

                CREATE TABLE scaffold_review.parent_single (
                    id integer PRIMARY KEY,
                    raw_email character varying(200) NOT NULL,
                    amount numeric(10,2) NOT NULL,
                    created_at timestamp(2) with time zone NOT NULL,
                    normalized_email text GENERATED ALWAYS AS (lower(raw_email)) STORED
                );
                COMMENT ON TABLE scaffold_review.parent_single IS 'parent table comment';
                COMMENT ON COLUMN scaffold_review.parent_single.raw_email IS 'raw email comment';
                CREATE UNIQUE INDEX uq_parent_single_raw_email ON scaffold_review.parent_single (raw_email);
                CREATE UNIQUE INDEX uq_parent_single_raw_email_include ON scaffold_review.parent_single (raw_email) INCLUDE (id);
                CREATE INDEX ix_parent_single_raw_email_partial ON scaffold_review.parent_single (raw_email) WHERE raw_email <> '';

                CREATE TABLE scaffold_review.child_single (
                    id integer PRIMARY KEY,
                    parent_single_id integer NOT NULL,
                    CONSTRAINT fk_child_single_parent_single_id
                        FOREIGN KEY (parent_single_id)
                        REFERENCES scaffold_review.parent_single(id)
                        ON DELETE CASCADE
                );

                CREATE TABLE scaffold_review.parent_composite (
                    id1 integer NOT NULL,
                    id2 integer NOT NULL,
                    status text NOT NULL DEFAULT 'draft',
                    PRIMARY KEY (id1, id2)
                );
                CREATE TABLE scaffold_review.child_composite (
                    id integer PRIMARY KEY,
                    id1 integer NOT NULL,
                    id2 integer NOT NULL,
                    FOREIGN KEY (id1, id2) REFERENCES scaffold_review.parent_composite(id1, id2)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = ScaffoldOptions.Parse(
        [
            "--connection", _fixture.ConnectionString,
            "--context", "ScaffoldReviewDbContext",
            "--namespace", "ScaffoldReview",
            "--output", "Generated",
            "--schema", "scaffold_review"
        ]);
        var database = await new PostgreSqlCatalogReader(_fixture.ConnectionString).ReadAsync(options);

        var parentSingle = Assert.Single(database.Tables, table => table.Name == "parent_single");
        Assert.Equal("parent table comment", parentSingle.Comment);
        Assert.True(parentSingle.Columns.Single(column => column.Name == "normalized_email").IsGenerated);
        var generatedExpression = parentSingle.Columns.Single(column => column.Name == "normalized_email").GeneratedExpression;
        Assert.NotNull(generatedExpression);
        Assert.Contains("lower", generatedExpression, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw_email", generatedExpression, StringComparison.OrdinalIgnoreCase);
        var rawEmail = parentSingle.Columns.Single(column => column.Name == "raw_email");
        Assert.Equal(200, rawEmail.MaxLength);
        Assert.Equal("raw email comment", rawEmail.Comment);
        var amount = parentSingle.Columns.Single(column => column.Name == "amount");
        Assert.Equal(10, amount.Precision);
        Assert.Equal(2, amount.Scale);
        var createdAt = parentSingle.Columns.Single(column => column.Name == "created_at");
        Assert.Equal(2, createdAt.Precision);
        var uniqueIndex = Assert.Single(parentSingle.Indexes, static index => index.Name == "uq_parent_single_raw_email");
        Assert.Equal(["raw_email"], uniqueIndex.ColumnNames);
        Assert.True(uniqueIndex.IsUnique);
        var includeIndex = Assert.Single(parentSingle.Indexes, static index => index.Name == "uq_parent_single_raw_email_include");
        Assert.Equal(["id"], includeIndex.IncludeColumnNames);
        var partialIndex = Assert.Single(parentSingle.Indexes, static index => index.Name == "ix_parent_single_raw_email_partial");
        Assert.NotNull(partialIndex.Filter);
        Assert.Contains("raw_email", partialIndex.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<>", partialIndex.Filter, StringComparison.Ordinal);

        var childSingle = Assert.Single(database.Tables, table => table.Name == "child_single");
        var foreignKey = Assert.Single(childSingle.ForeignKeys);
        Assert.Equal("fk_child_single_parent_single_id", foreignKey.ConstraintName);
        Assert.Equal("parent_single_id", foreignKey.ColumnName);
        Assert.Equal("parent_single", foreignKey.PrincipalTable);
        Assert.Equal("Cascade", foreignKey.OnDeleteAction);

        var childComposite = Assert.Single(database.Tables, table => table.Name == "child_composite");
        var compositeForeignKey = Assert.Single(childComposite.ForeignKeys);
        Assert.Equal(["id1", "id2"], compositeForeignKey.ColumnNames);
        Assert.Equal(["id1", "id2"], compositeForeignKey.PrincipalColumnNames);

        var parentComposite = Assert.Single(database.Tables, table => table.Name == "parent_composite");
        Assert.Equal(["id1", "id2"], parentComposite.PrimaryKeyColumns);
        Assert.Equal("'draft'::text", parentComposite.Columns.Single(column => column.Name == "status").DefaultValue);

        Assert.Empty(database.Warnings);
        Assert.DoesNotContain(database.Warnings, warning => warning.Contains("composite primary key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(database.Warnings, warning => warning.Contains("Composite foreign keys are not scaffolded yet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(database.Warnings, warning => warning.Contains("partial index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(database.Warnings, warning => warning.Contains("parent_composite.status", StringComparison.OrdinalIgnoreCase));
    }
}