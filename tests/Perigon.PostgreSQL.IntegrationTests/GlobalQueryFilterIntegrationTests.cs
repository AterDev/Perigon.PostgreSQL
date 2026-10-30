using System.ComponentModel.DataAnnotations.Schema;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class GlobalQueryFilterIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Named_filters_isolate_tenants_and_bound_update_and_delete()
    {
        var schema = fixture.CreateIsolatedSchema("global_filters_crud");
        var marker = "filter-crud-" + Guid.NewGuid().ToString("N");
        await using var tenantOne = new GlobalFilterDbContext(fixture.ConnectionString, schema, tenantId: 1);
        await tenantOne.EnsureCreatedAsync();

        var tenantOneLive = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 1,
            Name = marker + "-tenant-one-live"
        });
        var tenantOneDeleted = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 1,
            IsDeleted = true,
            Name = marker + "-tenant-one-deleted"
        });
        var tenantTwoLive = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 2,
            Name = marker + "-tenant-two-live"
        });
        _ = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 2,
            IsDeleted = true,
            Name = marker + "-tenant-two-deleted"
        });

        var visibleToTenantOne = await tenantOne.Parents
            .Where(parent => parent.Name.StartsWith(marker))
            .ToListAsync();
        var visibleTenantOneRow = Assert.Single(visibleToTenantOne);
        Assert.Equal(tenantOneLive.Id, visibleTenantOneRow.Id);

        var updatedName = marker + "-updated";
        var hiddenName = marker + "-should-not-update";
        var updated = await tenantOne.Parents
            .Where(parent => parent.Id == tenantOneLive.Id)
            .ExecuteUpdateAsync(setter => setter.Set(parent => parent.Name, updatedName));
        Assert.Equal(1, updated);

        var hiddenUpdate = await tenantOne.Parents
            .Where(parent => parent.Id == tenantOneDeleted.Id)
            .ExecuteUpdateAsync(setter => setter.Set(parent => parent.Name, hiddenName));
        Assert.Equal(0, hiddenUpdate);

        var deleted = await tenantOne.Parents
            .Where(parent => parent.Id == tenantOneLive.Id)
            .ExecuteDeleteAsync();
        Assert.Equal(1, deleted);

        await using var tenantTwo = new GlobalFilterDbContext(fixture.ConnectionString, schema, tenantId: 2);
        var visibleToTenantTwo = await tenantTwo.Parents
            .Where(parent => parent.Name.StartsWith(marker))
            .ToListAsync();
        var visibleTenantTwoRow = Assert.Single(visibleToTenantTwo);
        Assert.Equal(tenantTwoLive.Id, visibleTenantTwoRow.Id);

        var allRows = await tenantOne.SetIgnoringAllFilters<GlobalFilterParent>()
            .Where(parent => parent.Name.StartsWith(marker))
            .ToListAsync();
        Assert.Equal(3, allRows.Count);
        Assert.Contains(allRows, parent => parent.Id == tenantOneDeleted.Id);
        Assert.Contains(allRows, parent => parent.Id == tenantTwoLive.Id);
    }

    [Fact]
    public async Task Include_applies_child_filters_and_propagates_soft_delete_scope_to_cached_child_set()
    {
        var schema = fixture.CreateIsolatedSchema("global_filters_include");
        await using var db = new GlobalFilterDbContext(fixture.ConnectionString, schema, tenantId: 1);
        await db.EnsureCreatedAsync();

        var parent = await db.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 1,
            Name = "include-parent"
        });

        _ = await db.Children.InsertManyReturningAsync(
        [
            new GlobalFilterChild
            {
                ParentId = parent.Id,
                TenantId = 1,
                Name = "visible-child"
            },
            new GlobalFilterChild
            {
                ParentId = parent.Id,
                TenantId = 1,
                IsDeleted = true,
                Name = "soft-deleted-child"
            },
            new GlobalFilterChild
            {
                ParentId = parent.Id,
                TenantId = 2,
                Name = "other-tenant-child"
            }
        ]);

        var cachedChildren = db.Children;
        var filtered = await db.Parents
            .Where(item => item.Id == parent.Id)
            .IncludeManyAsync(
                cachedChildren,
                item => item.Id,
                child => child.ParentId,
                (item, children) => new GlobalFilterGraph(item, children));

        var filteredGraph = Assert.Single(filtered);
        Assert.Equal(["visible-child"], filteredGraph.Children.Select(child => child.Name));

        var joined = await db.Parents
            .Where(item => item.Id == parent.Id)
            .Join(db.Children, item => item.Id, child => child.ParentId,
                (item, child) => new GlobalFilterJoin { ParentId = item.Id, ChildName = child.Name })
            .ToListAsync();
        Assert.Equal(["visible-child"], joined.Select(item => item.ChildName));

        var navigationInclude = await db.SetWithoutFilters<GlobalFilterParent>("SoftDelete")
            .Where(item => item.Id == parent.Id)
            .Include(item => item.Children)
            .ToListAsync();
        var navigationParent = Assert.Single(navigationInclude);
        Assert.Equal(
            ["soft-deleted-child", "visible-child"],
            navigationParent.Children.Select(child => child.Name).Order());

        using var softDeleteScope = ((IQueryFilterContext)db).PushIgnoredFilters(
            new HashSet<string>(["SoftDelete"], StringComparer.Ordinal));
        var cachedChildWithScope = await db.SetWithoutFilters<GlobalFilterParent>("SoftDelete")
            .Where(item => item.Id == parent.Id)
            .IncludeManyAsync(
                cachedChildren,
                item => item.Id,
                child => child.ParentId,
                (item, children) => new GlobalFilterGraph(item, children));

        var scopedGraph = Assert.Single(cachedChildWithScope);
        Assert.Equal(
            ["soft-deleted-child", "visible-child"],
            scopedGraph.Children.Select(child => child.Name).Order());
    }
}

public sealed class GlobalFilterDbContext : DbContext
{
    private readonly string _schema;

    public GlobalFilterDbContext(string connectionString, string schema, int tenantId)
        : base(options => options.UsePostgres(connectionString))
    {
        _schema = schema;
        TenantId = tenantId;
    }

    public int TenantId { get; set; }

    public DbSet<GlobalFilterParent> Parents => Set<GlobalFilterParent>();

    public DbSet<GlobalFilterChild> Children => Set<GlobalFilterChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalFilterParent>().ToTable("global_filter_parents", _schema);
        modelBuilder.Entity<GlobalFilterChild>().ToTable("global_filter_children", _schema);

        HasQueryFilter<GlobalFilterParent>("Tenant", parent => parent.TenantId == TenantId);
        HasQueryFilter<GlobalFilterParent>("SoftDelete", parent => !parent.IsDeleted);
        HasQueryFilter<GlobalFilterChild>("Tenant", child => child.TenantId == TenantId);
        HasQueryFilter<GlobalFilterChild>("SoftDelete", child => !child.IsDeleted);
    }
}

[Table("global_filter_parents")]
public sealed class GlobalFilterParent
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public bool IsDeleted { get; set; }

    public string Name { get; set; } = "";

    [NotMapped]
    public List<GlobalFilterChild> Children { get; set; } = [];
}

[Table("global_filter_children")]
public sealed class GlobalFilterChild
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public int TenantId { get; set; }

    public bool IsDeleted { get; set; }

    public string Name { get; set; } = "";

    [NotMapped, ForeignKey(nameof(ParentId))]
    public GlobalFilterParent? Parent { get; set; }
}

public sealed record GlobalFilterGraph(GlobalFilterParent Parent, List<GlobalFilterChild> Children);

public sealed class GlobalFilterJoin
{
    public int ParentId { get; set; }
    public string ChildName { get; set; } = "";
}
