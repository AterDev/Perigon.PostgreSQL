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

    [Fact]
    public async Task Correlated_any_with_nested_any_keeps_tenant_soft_delete_filters_and_database_paging()
    {
        var schema = fixture.CreateIsolatedSchema("global_filters_correlated_any");
        var marker = "correlated-any-" + Guid.NewGuid().ToString("N");
        var candidateName = marker + "-candidate";
        var qualifierName = marker + "-qualifier";
        await using var tenantOne = new GlobalFilterDbContext(fixture.ConnectionString, schema, tenantId: 1);
        await tenantOne.EnsureCreatedAsync();

        var tenantOneMatch = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 1,
            Name = marker + "-tenant-one-match"
        });
        var tenantOneOther = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 1,
            Name = marker + "-tenant-one-other"
        });
        var tenantTwoMatch = await tenantOne.Parents.InsertAsync(new GlobalFilterParent
        {
            TenantId = 2,
            Name = marker + "-tenant-two-match"
        });

        _ = await tenantOne.Children.InsertManyReturningAsync(
        [
            new GlobalFilterChild
            {
                ParentId = tenantOneMatch.Id,
                TenantId = 1,
                Name = marker + "-candidate"
            },
            new GlobalFilterChild
            {
                ParentId = tenantOneMatch.Id,
                TenantId = 1,
                Name = marker + "-qualifier"
            },
            new GlobalFilterChild
            {
                ParentId = tenantOneOther.Id,
                TenantId = 1,
                Name = marker + "-candidate"
            },
            new GlobalFilterChild
            {
                ParentId = tenantOneOther.Id,
                TenantId = 1,
                IsDeleted = true,
                Name = marker + "-qualifier"
            },
            new GlobalFilterChild
            {
                ParentId = tenantTwoMatch.Id,
                TenantId = 2,
                Name = marker + "-candidate"
            },
            new GlobalFilterChild
            {
                ParentId = tenantTwoMatch.Id,
                TenantId = 2,
                Name = marker + "-qualifier"
            }
        ]);

        var candidateChildren = tenantOne.Children.Where(child =>
            child.Name == candidateName &&
            tenantOne.Children.Any(inner =>
                inner.ParentId == child.ParentId &&
                inner.Name == qualifierName));

        var capturedQuery = tenantOne.Parents
            .Where(parent => candidateChildren.Any(child => child.ParentId == parent.Id))
            .OrderBy(parent => parent.Id)
            .Skip(0)
            .Take(1);

        Assert.Equal(1, await capturedQuery.CountAsync());
        var capturedPage = await capturedQuery.ToListAsync();
        Assert.Equal(tenantOneMatch.Id, Assert.Single(capturedPage).Id);

        var inlineQuery = tenantOne.Parents
            .Where(parent => tenantOne.Children
                .Where(child => child.Name == candidateName)
                .Any(child => child.ParentId == parent.Id &&
                    tenantOne.Children.Any(inner =>
                        inner.ParentId == child.ParentId &&
                        inner.Name == qualifierName)))
            .OrderBy(parent => parent.Id)
            .Skip(0)
            .Take(1);

        Assert.Equal(1, await inlineQuery.CountAsync());
        Assert.Equal(tenantOneMatch.Id, Assert.Single(await inlineQuery.ToListAsync()).Id);

        await using var tenantTwo = new GlobalFilterDbContext(fixture.ConnectionString, schema, tenantId: 2);
        var tenantTwoQuery = tenantTwo.Parents
            .Where(parent => tenantTwo.Children.Any(child =>
                child.Name == candidateName &&
                child.ParentId == parent.Id &&
                tenantTwo.Children.Any(inner =>
                    inner.ParentId == child.ParentId &&
                    inner.Name == qualifierName)))
            .OrderBy(parent => parent.Id)
            .Skip(0)
            .Take(1);

        Assert.Equal(1, await tenantTwoQuery.CountAsync());
        Assert.Equal(tenantTwoMatch.Id, Assert.Single(await tenantTwoQuery.ToListAsync()).Id);
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
