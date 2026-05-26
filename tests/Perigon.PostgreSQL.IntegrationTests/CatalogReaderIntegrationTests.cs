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
    public async Task Catalog_reader_keeps_simple_objects_and_skips_unsupported_shapes()
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
                    raw_email text NOT NULL,
                    normalized_email text GENERATED ALWAYS AS (lower(raw_email)) STORED
                );
                CREATE UNIQUE INDEX uq_parent_single_raw_email ON scaffold_review.parent_single (raw_email);
                CREATE UNIQUE INDEX uq_parent_single_raw_email_include ON scaffold_review.parent_single (raw_email) INCLUDE (id);

                CREATE TABLE scaffold_review.child_single (
                    id integer PRIMARY KEY,
                    parent_single_id integer NOT NULL REFERENCES scaffold_review.parent_single(id)
                );

                CREATE TABLE scaffold_review.parent_composite (
                    id1 integer NOT NULL,
                    id2 integer NOT NULL,
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
        Assert.True(parentSingle.Columns.Single(column => column.Name == "normalized_email").IsGenerated);
        var index = Assert.Single(parentSingle.Indexes);
        Assert.Equal("uq_parent_single_raw_email", index.Name);
        Assert.Equal(["raw_email"], index.ColumnNames);
        Assert.True(index.IsUnique);

        var childSingle = Assert.Single(database.Tables, table => table.Name == "child_single");
        var foreignKey = Assert.Single(childSingle.ForeignKeys);
        Assert.Equal("parent_single_id", foreignKey.ColumnName);
        Assert.Equal("parent_single", foreignKey.PrincipalTable);

        var childComposite = Assert.Single(database.Tables, table => table.Name == "child_composite");
        Assert.Empty(childComposite.ForeignKeys);
    }
}