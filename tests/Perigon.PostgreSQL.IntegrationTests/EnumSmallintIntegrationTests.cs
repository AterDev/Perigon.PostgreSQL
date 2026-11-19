using Npgsql;
using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

public enum EnumStorageStatus
{
    Unknown,
    Active,
    Archived,
}

public sealed class EnumStorageRow
{
    public Guid Id { get; set; }

    public EnumStorageStatus Status { get; set; }

    public EnumStorageStatus? OptionalStatus { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class EnumStorageContext(string connectionString) : DbContext(builder => builder.UsePostgres(connectionString))
{
    public DbSet<EnumStorageRow> Rows => Set<EnumStorageRow>();
}

[Collection(PostgresCollection.Name)]
public sealed class EnumSmallintIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Enum_properties_use_smallint_and_round_trip_without_shadow_fields()
    {
        await using var db = new EnumStorageContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var statusColumnType = await ReadColumnTypeAsync(db, nameof(EnumStorageRow.Status));
        var optionalStatusColumnType = await ReadColumnTypeAsync(db, nameof(EnumStorageRow.OptionalStatus));
        Assert.Equal("smallint", statusColumnType);
        Assert.Equal("smallint", optionalStatusColumnType);

        var active = await db.Rows.InsertAsync(new EnumStorageRow
        {
            Id = Guid.NewGuid(),
            Status = EnumStorageStatus.Active,
            OptionalStatus = EnumStorageStatus.Archived,
            Name = "active",
        });
        await db.Rows.BulkInsertAsync(
        [
            new EnumStorageRow { Id = Guid.NewGuid(), Status = EnumStorageStatus.Unknown, Name = "unknown" }
        ]);

        var filtered = await db.Rows
            .Where(row => row.Status == EnumStorageStatus.Active && row.OptionalStatus == EnumStorageStatus.Archived)
            .ToListAsync();

        var row = Assert.Single(filtered);
        Assert.Equal(active.Id, row.Id);
        Assert.Equal(EnumStorageStatus.Active, row.Status);
        Assert.Equal(EnumStorageStatus.Archived, row.OptionalStatus);

        var archived = EnumStorageStatus.Archived;
        Assert.Equal(1, await db.Rows.Where(item => item.OptionalStatus == null)
            .ExecuteUpdateAsync(set => set.Set(item => item.Status, archived)));
        var scalars = await db.Rows.Where(item => item.Name == "unknown").Select(item => item.Status).ToScalarListAsync();
        Assert.Equal(archived, Assert.Single(scalars));

        await Assert.ThrowsAsync<OverflowException>(() => db.Rows.InsertAsync(new EnumStorageRow
        {
            Id = Guid.NewGuid(),
            Status = (EnumStorageStatus)(short.MaxValue + 1)
        }));
    }

    private static async Task<string?> ReadColumnTypeAsync(EnumStorageContext db, string propertyName)
    {
        await using var connection = await db.GetDataSource().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = $1
              AND column_name = $2
            """;
        command.Parameters.AddWithValue(db.Rows.Model.TableName);
        command.Parameters.AddWithValue(db.Rows.Model.GetColumn(propertyName).ColumnName);
        return (string?)await command.ExecuteScalarAsync();
    }
}
