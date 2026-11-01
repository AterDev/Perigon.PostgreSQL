using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class ModelCacheTests
{
    [Fact]
    public void Migration_model_rejects_a_concurrent_context_operation()
    {
        using var db = new ModelCacheContext(tenantId: 7);
        using var operation = db.EnterOperation();
        Assert.Throws<InvalidOperationException>(() => db.GetMigrationModel());
    }

    [Fact]
    public void Model_and_dbset_are_cached_for_the_lifetime_of_a_context()
    {
        using var db = new ModelCacheContext(tenantId: 7);

        _ = db.GetMigrationModel();
        var first = db.Rows;
        var second = db.Rows;
        _ = db.OtherRows;

        Assert.Same(first, second);
        Assert.Equal(1, db.ModelCreatingCalls);
    }

    [Fact]
    public void Cached_filter_expression_still_reads_the_current_context_value()
    {
        using var db = new ModelCacheContext(tenantId: 7);

        var first = db.Rows.ToQuerySql();
        db.TenantId = 11;
        var second = db.Rows.ToQuerySql();

        Assert.Equal(7, first.Parameters.Single().Value);
        Assert.Equal(11, second.Parameters.Single().Value);
        Assert.Equal(1, db.ModelCreatingCalls);
    }
}

public sealed class ModelCacheContext(int tenantId) : DbContext
{
    public int TenantId { get; set; } = tenantId;

    public int ModelCreatingCalls { get; private set; }

    public DbSet<ModelCacheRow> Rows => Set<ModelCacheRow>();

    public DbSet<ModelCacheOtherRow> OtherRows => Set<ModelCacheOtherRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ModelCreatingCalls++;
        HasQueryFilter<ModelCacheRow>("Tenant", row => row.TenantId == TenantId);
    }
}

public sealed class ModelCacheRow
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class ModelCacheOtherRow
{
    public int Id { get; set; }
}
