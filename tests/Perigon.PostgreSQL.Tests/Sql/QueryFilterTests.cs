using System.ComponentModel.DataAnnotations.Schema;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class QueryFilterTests
{
    [Fact]
    public void Queries_created_in_an_ignored_filter_scope_expire_with_the_scope()
    {
        using var db = new FilterDbContext(tenantId: 7);
        DbSet<FilterRow> scopedSet;
        IQueryable<FilterRow> scopedQuery;
        using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"])))
        {
            scopedSet = db.Rows;
            scopedQuery = scopedSet.Where(row => row.Id > 0);
            Assert.DoesNotContain(scopedQuery.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 7));
        }

        Assert.Throws<InvalidOperationException>(() => scopedSet.ToQuerySql());
        Assert.Throws<InvalidOperationException>(() => scopedQuery.ToQuerySql());
        Assert.Contains(db.Rows.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public async Task Ignored_filter_scope_cannot_escape_into_a_delayed_child_task()
    {
        using var db = new FilterDbContext(tenantId: 7);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception> child;
        using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"])))
        {
            var query = db.Rows.Where(row => row.Id > 0);
            child = Task.Run(async () =>
            {
                ready.SetResult();
                await resume.Task;
                return Record.Exception(() => query.ToQuerySql());
            });
            await ready.Task;
        }
        resume.SetResult();
        Assert.IsType<InvalidOperationException>(await child);
    }

    [Fact]
    public void Nested_filter_scopes_keep_outer_queries_valid_only_while_their_scope_is_active()
    {
        using var db = new FilterDbContext(tenantId: 7);
        using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"])))
        {
            var outer = db.Rows;
            using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["SoftDelete"])))
            {
                _ = outer.ToQuerySql();
                var inner = db.Rows.ToQuerySql();
                Assert.DoesNotContain(inner.Parameters, parameter => Equals(parameter.Value, 7));
                Assert.DoesNotContain("NOT", inner.CommandText);
            }
            _ = outer.ToQuerySql();
        }
    }

    [Fact]
    public void Explicit_unfiltered_set_is_not_bound_to_an_ambient_scope()
    {
        using var db = new FilterDbContext(tenantId: 7);
        DbSet<FilterRow> unfiltered;
        DbSet<FilterRow> partiallyFiltered;
        using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"])))
        {
            unfiltered = db.SetIgnoringAllFilters<FilterRow>();
            partiallyFiltered = db.SetWithoutFilters<FilterRow>("SoftDelete");
        }
        _ = unfiltered.ToQuerySql();
        Assert.Throws<InvalidOperationException>(() => partiallyFiltered.ToQuerySql());
    }

    [Fact]
    public void Filter_scope_snapshots_the_supplied_names()
    {
        using var db = new FilterDbContext(tenantId: 7);
        var names = new HashSet<string>(["SoftDelete"]);
        using var scope = ((IQueryFilterContext)db).PushIgnoredFilters(names);
        names.Add("Tenant");
        Assert.Contains(db.Rows.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public void Disposing_filter_scopes_out_of_order_invalidates_children_and_restores_normal_queries()
    {
        using var db = new FilterDbContext(tenantId: 7);
        var outer = ((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"]));
        var inner = ((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["SoftDelete"]));
        outer.Dispose();
        Assert.Throws<InvalidOperationException>(() => db.Rows.ToQuerySql());
        inner.Dispose();
        Assert.Contains(db.Rows.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 7));
    }

    [Fact]
    public async Task Scope_bound_query_cannot_be_used_without_its_execution_context()
    {
        using var db = new FilterDbContext(tenantId: 7);
        using var scope = ((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"]));
        var query = db.Rows.Where(row => row.Id > 0);
        Task<Exception> child;
        using (ExecutionContext.SuppressFlow())
            child = Task.Run(() => Record.Exception(() => query.ToQuerySql()));
        Assert.IsType<InvalidOperationException>(await child);
    }

    [Fact]
    public void Join_cannot_escape_through_an_unscoped_outer_provider()
    {
        using var db = new FilterDbContext(tenantId: 7);
        var outer = db.Rows;
        IQueryable<int> joined;
        using (((IQueryFilterContext)db).PushIgnoredFilters(new HashSet<string>(["Tenant"])))
        {
            var inner = db.Rows;
            joined = outer.Join(inner, left => left.Id, right => right.Id, (left, right) => left.Id);
            _ = joined.ToSql();
        }
        Assert.Throws<InvalidOperationException>(() => joined.ToSql());
    }

    [Fact]
    public void Filters_are_registered_without_generated_context_metadata()
    {
        using var db = new FallbackFilterDbContext();
        var sql = db.Rows().ToQuerySql();
        Assert.Contains("\"tenant_id\"", sql.CommandText);
        Assert.Equal(17, sql.Parameters[0].Value);
    }

    [Fact]
    public void Named_filters_are_applied_to_query_update_and_delete_roots()
    {
        using var db = new FilterDbContext(tenantId: 7);

        var querySql = db.Rows.Where(row => row.Id > 10).ToQuerySql();
        Assert.Contains("WHERE", querySql.CommandText);
        Assert.Contains("\"tenant_id\"", querySql.CommandText);
        Assert.Contains("\"is_deleted\"", querySql.CommandText);
        Assert.Equal(7, querySql.Parameters[0].Value);

        var updateSql = db.Rows.Where(row => row.Id > 10)
            .ToUpdateSql(setter => setter.Set(row => row.Name, "updated"));
        Assert.Contains("\"tenant_id\"", updateSql.CommandText);
        Assert.Contains("\"is_deleted\"", updateSql.CommandText);

        var deleteSql = db.Rows.Where(row => row.Id > 10).ToDeleteSql();
        Assert.Contains("\"tenant_id\"", deleteSql.CommandText);
        Assert.Contains("\"is_deleted\"", deleteSql.CommandText);
    }

    [Fact]
    public void Dynamic_filter_values_are_read_from_each_context()
    {
        using var first = new FilterDbContext(tenantId: 7);
        using var second = new FilterDbContext(tenantId: 9);

        var firstSql = first.Rows.ToQuerySql();
        var secondSql = second.Rows.ToQuerySql();

        Assert.Equal(7, firstSql.Parameters[0].Value);
        Assert.Equal(9, secondSql.Parameters[0].Value);

        first.TenantId = 11;
        var rebuiltSql = first.Rows.ToQuerySql();
        Assert.Equal(11, rebuiltSql.Parameters[0].Value);
    }

    [Fact]
    public void Named_filter_can_be_disabled_without_disabling_other_filters()
    {
        using var db = new FilterDbContext(tenantId: 7);

        var sql = db.SetWithoutFilters<FilterRow>("SoftDelete").ToQuerySql();

        Assert.Contains("\"tenant_id\"", sql.CommandText);
        Assert.DoesNotContain("NOT", sql.CommandText);
        Assert.Equal(7, sql.Parameters[0].Value);
    }

    [Fact]
    public void Correlated_any_keeps_query_filters_and_outer_aliases()
    {
        using var db = new FilterDbContext(tenantId: 7);
        int[] allowedOwners = [11, 12];

        var visiblePosts = db.Posts.Where(post =>
            allowedOwners.Contains(post.OwnerId) &&
            db.Posts.Any(inner => inner.OwnerId == post.OwnerId));

        var sql = db.Owners
            .Where(owner => visiblePosts.Any(post => post.OwnerId == owner.Id))
            .OrderByDescending(owner => owner.Id)
            .Skip(3)
            .Take(2)
            .ToQuerySql();

        Assert.Contains("EXISTS (SELECT 1 FROM \"filter_posts\" AS s", sql.CommandText);
        Assert.Contains("EXISTS (SELECT 1 FROM \"filter_posts\" AS ss", sql.CommandText);
        Assert.Contains("s.\"owner_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Contains("ss.\"owner_id\" IS NOT DISTINCT FROM s.\"owner_id\"", sql.CommandText);
        Assert.Contains("s.\"owner_id\" = ANY", sql.CommandText);
        Assert.Equal(3, sql.CommandText.Split("\"tenant_id\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, sql.CommandText.Split("\"is_deleted\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("ORDER BY e.\"id\" DESC LIMIT", sql.CommandText);
        Assert.Contains("OFFSET", sql.CommandText);
        Assert.Equal(5, sql.Parameters.Count);
    }

    [Fact]
    public void Include_child_query_keeps_child_global_filters()
    {
        using var db = new FilterDbContext(tenantId: 7);
        var childQuery = QueryModelFactory.Create(
            db.Posts.Where(post => post.Id > 2).Expression);
        var sql = IncludeManyExtensions.BuildChildrenSql(
            db.Posts,
            db.Posts.Model.GetColumn(nameof(FilterPost.OwnerId)),
            new[] { 1 },
            childQuery,
            applyGlobalFilters: false);

        Assert.Contains("\"tenant_id\"", sql.CommandText);
        Assert.Contains("\"is_deleted\"", sql.CommandText);
        Assert.Contains("\"id\"", sql.CommandText);
    }

    [Fact]
    public void Include_child_query_honors_the_parent_ignored_filter_scope()
    {
        using var db = new FilterDbContext(tenantId: 7);
        using var scope = ((IQueryFilterContext)db).PushIgnoredFilters(
            new HashSet<string>(["SoftDelete"], StringComparer.Ordinal));
        var childQuery = QueryModelFactory.Create(db.Posts.Where(post => post.Id > 2).Expression);
        var sql = IncludeManyExtensions.BuildChildrenSql(
            db.Posts,
            db.Posts.Model.GetColumn(nameof(FilterPost.OwnerId)),
            new[] { 1 },
            childQuery,
            applyGlobalFilters: false);

        Assert.Contains("\"tenant_id\"", sql.CommandText);
        Assert.DoesNotContain("NOT", sql.CommandText);
    }
}

public sealed class FallbackFilterDbContext : DbContext
{
    public DbSet<FilterRow> Rows() => Set<FilterRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => HasQueryFilter<FilterRow>("Tenant", row => row.TenantId == 17);
}

public sealed class FilterDbContext(int tenantId) : DbContext
{
    public int TenantId { get; set; } = tenantId;

    public DbSet<FilterRow> Rows => Set<FilterRow>();

    public DbSet<FilterOwner> Owners => Set<FilterOwner>();

    public DbSet<FilterPost> Posts => Set<FilterPost>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        HasQueryFilter<FilterRow>("Tenant", row => row.TenantId == TenantId);
        HasQueryFilter<FilterRow>("SoftDelete", row => !row.IsDeleted);
        HasQueryFilter<FilterPost>("Tenant", post => post.TenantId == TenantId);
        HasQueryFilter<FilterPost>("SoftDelete", post => !post.IsDeleted);
    }
}

public sealed class FilterRow
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public bool IsDeleted { get; set; }

    public string Name { get; set; } = "";
}

public sealed class FilterOwner
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public bool IsDeleted { get; set; }

    [NotMapped]
    public List<FilterPost> Posts { get; set; } = [];
}

public sealed class FilterPost
{
    public int Id { get; set; }

    public int OwnerId { get; set; }

    public int TenantId { get; set; }

    public bool IsDeleted { get; set; }

    [NotMapped]
    public FilterOwner? Owner { get; set; }
}
