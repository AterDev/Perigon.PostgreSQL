using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Bulk;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class KeyGenerationIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public KeyGenerationIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Guid_primary_key_and_inherited_timestamps_are_generated_on_insert()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var before = DateTimeOffset.UtcNow;
        var row = await db.GuidGenerationRows.InsertAsync(new GuidGenerationRow { Code = "guid-single" });
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(7, row.Id.Version);
        Assert.InRange(row.CreatedTime, before, after);
        Assert.InRange(row.UpdatedTime, before, after);
    }

    [Fact]
    public async Task Guid_primary_keys_are_generated_for_values_and_copy_and_explicit_values_are_preserved()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var explicitId = Guid.CreateVersion7();
        var values = await db.GuidGenerationRows.InsertManyReturningAsync(
        [
            new GuidGenerationRow { Code = "guid-values-generated" },
            new GuidGenerationRow { Id = explicitId, Code = "guid-values-explicit" }
        ]);

        Assert.NotEqual(Guid.Empty, values[0].Id);
        Assert.Equal(7, values[0].Id.Version);
        Assert.Equal(explicitId, values[1].Id);

        var copy = new[]
        {
            new GuidGenerationRow { Code = "guid-copy-generated" },
            new GuidGenerationRow { Code = "guid-copy-generated-2" }
        };
        await db.GuidGenerationRows.BulkInsertAsync(copy);

        Assert.All(copy, row =>
        {
            Assert.NotEqual(Guid.Empty, row.Id);
            Assert.Equal(7, row.Id.Version);
        });
    }

    [Fact]
    public async Task Upsert_preserves_guid_primary_key_and_string_keys_require_values()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var first = await db.GuidGenerationRows.InsertAsync(new GuidGenerationRow
        {
            Code = "guid-upsert",
            Value = "before"
        });

        var affected = await db.GuidGenerationRows.UpsertManyAsync(
            [new GuidGenerationRow { Code = "guid-upsert", Value = "after" }],
            row => row.Code);

        Assert.Equal(1, affected);
        var updated = (await db.GuidGenerationRows
            .Where(row => row.Code == "guid-upsert")
            .ToListAsync()).Single();
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("after", updated.Value);

        var stringRow = await db.StringGenerationRows.InsertAsync(
            new StringGenerationRow { Id = "string-key", Value = "ok" });
        Assert.Equal("string-key", stringRow.Id);
    }

    [Fact]
    public async Task Upsert_insert_generates_guid_v7_primary_key()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var code = "guid-upsert-insert-" + Guid.NewGuid().ToString("N");
        var row = new GuidGenerationRow { Code = code, Value = "inserted" };

        var affected = await db.GuidGenerationRows.UpsertManyAsync([row], item => item.Code);

        Assert.Equal(1, affected);
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(7, row.Id.Version);

        var persisted = Assert.Single(await db.GuidGenerationRows
            .Where(item => item.Code == code)
            .ToListAsync());
        Assert.Equal(row.Id, persisted.Id);
        Assert.Equal("inserted", persisted.Value);
    }

    [Fact]
    public async Task Long_identity_primary_key_is_generated_by_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var row = await db.LongGenerationRows.InsertAsync(new LongGenerationRow { Code = "long-key" });

        Assert.True(row.Id > 0);
    }
}
