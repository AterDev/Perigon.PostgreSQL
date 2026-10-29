using System.ComponentModel.DataAnnotations.Schema;
using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NullableAggregateIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Nullable_min_max_average_handle_all_null_and_mixed_rows()
    {
        await using var db = new NullableAggregateDbContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var allNull = await db.NullableAggregateParents.InsertAsync(new NullableAggregateParent { Name = "all-null" });

        var mixed = await db.NullableAggregateParents.InsertAsync(new NullableAggregateParent { Name = "mixed" });
        await db.NullableAggregateValues.InsertManyReturningAsync(
        [
            new NullableAggregateValue { ParentId = mixed.Id, Value = null },
            new NullableAggregateValue { ParentId = mixed.Id, Value = 10 },
            new NullableAggregateValue { ParentId = mixed.Id, Value = 20 }
        ]);

        var nullRow = await db.NullableAggregateParents
            .Where(parent => parent.Id == allNull.Id)
            .Select(parent => new NullableAggregateResult
            {
                Min = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Min(value => value.Value),
                Max = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Max(value => value.Value),
                Average = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Average(value => (double?)value.Value)
            })
            .FirstOrDefaultAsync();
        Assert.NotNull(nullRow);
        Assert.Null(nullRow!.Min);
        Assert.Null(nullRow.Max);
        Assert.Null(nullRow.Average);

        var mixedRow = await db.NullableAggregateParents
            .Where(parent => parent.Id == mixed.Id)
            .Select(parent => new NullableAggregateResult
            {
                Min = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Min(value => value.Value),
                Max = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Max(value => value.Value),
                Average = db.NullableAggregateValues.Where(value => value.ParentId == parent.Id).Average(value => (double?)value.Value)
            })
            .FirstOrDefaultAsync();
        Assert.NotNull(mixedRow);
        Assert.Equal(10, mixedRow!.Min);
        Assert.Equal(20, mixedRow.Max);
        Assert.Equal(15d, mixedRow.Average);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.NullableAggregateParents
                .Where(parent => parent.Id == allNull.Id)
                .Select(parent => new NullableAggregateNonNullableResult
                {
                    Min = db.NullableAggregateValues
                        .Where(value => value.ParentId == parent.Id)
                        .Min(value => value.Id)
                })
                .ToListAsync());

        var mixedNonNullRows = await db.NullableAggregateParents
            .Where(parent => parent.Id == mixed.Id)
            .Select(parent => new NullableAggregateNonNullableResult
            {
                Min = db.NullableAggregateValues
                    .Where(value => value.ParentId == parent.Id)
                    .Min(value => value.Id)
            })
            .ToListAsync();
        var mixedNonNull = Assert.Single(mixedNonNullRows);
        Assert.True(mixedNonNull!.Min > 0);
    }
}

public sealed class NullableAggregateDbContext(string connectionString) : DbContext(builder => builder.UsePostgres(connectionString))
{
    public DbSet<NullableAggregateParent> NullableAggregateParents => Set<NullableAggregateParent>();

    public DbSet<NullableAggregateValue> NullableAggregateValues => Set<NullableAggregateValue>();
}

[Table("nullable_aggregate_parents")]
public sealed class NullableAggregateParent
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

[Table("nullable_aggregate_values")]
public sealed class NullableAggregateValue
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public int? Value { get; set; }
}

public sealed class NullableAggregateResult
{
    public int? Min { get; set; }

    public int? Max { get; set; }

    public double? Average { get; set; }
}

public sealed class NullableAggregateNonNullableResult
{
    public int Min { get; set; }
}
