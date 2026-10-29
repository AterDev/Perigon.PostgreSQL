using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class NavigationIncludeTests
{
    [Fact]
    public void Include_is_registered_and_preserves_filter_and_paging_in_query_chain()
    {
        using var db = new NavigationIncludeDbContext();
        Assert.NotNull(NavigationIncludeRegistry.GetLoader<IncludeOwner>(typeof(NavigationIncludeDbContext), nameof(IncludeOwner.Posts)));
        var sql = db.Owners.Include(owner => owner.Posts).Where(owner => owner.Id > 3)
            .OrderBy(owner => owner.Id).Take(2).ToQuerySql();
        Assert.Contains("WHERE", sql.CommandText);
        Assert.Contains("ORDER BY", sql.CommandText);
        Assert.Contains("LIMIT", sql.CommandText);
        Assert.Equal(new object?[] { 3, 2 }, sql.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void Include_rejects_unknown_navigation_and_projection()
    {
        using var db = new NavigationIncludeDbContext();
        Assert.Throws<InvalidOperationException>(() => db.Owners.Include(owner => owner.Name));
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Include(owner => owner.Posts).Select(owner => owner.Id));
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Select(owner => new IncludeOwner { Id = owner.Id }).Include(owner => owner.Posts));
    }

    [Fact]
    public async Task Generated_loaders_resolve_current_context_relationship_without_database_access_for_empty_keys()
    {
        using var db = new NavigationIncludeDbContext();
        await NavigationIncludeRegistry.GetLoader<IncludeOwner>(typeof(NavigationIncludeDbContext), nameof(IncludeOwner.Posts))(db, [], null, CancellationToken.None);
        var post = new IncludePost { IncludeOwnerId = null };
        await NavigationIncludeRegistry.GetLoader<IncludePost>(typeof(NavigationIncludeDbContext), nameof(IncludePost.IncludeOwner))(db, [post], null, CancellationToken.None);
        Assert.Null(post.IncludeOwner);
    }

    [Fact]
    public void Filtered_include_generates_partitioned_sql_and_parameters()
    {
        using var db = new NavigationIncludeDbContext();
        var prefix = "draft";
        Expression<Func<IncludeOwner, IEnumerable<IncludePost>>> navigation = owner => owner.Posts
            .Where(post => post.Title == prefix).OrderBy(post => post.Title).Skip(1).Take(2);
        _ = db.Owners.Include(navigation).ToSql();
        var model = FilteredIncludeParser.Parse(navigation.Body, typeof(IncludePost));
        var sql = IncludeManyExtensions.BuildChildrenSql(db.Posts, db.Posts.Model.GetColumn(nameof(IncludePost.IncludeOwnerId)), new[] { 1, 2 }, model, useCSharpNullSemantics: true);
        Assert.Contains("PARTITION BY e.\"include_owner_id\" ORDER BY e.\"title\" ASC, e.\"id\" ASC", sql.CommandText);
        Assert.Contains("e.\"title\" IS NOT DISTINCT FROM $2", sql.CommandText);
        Assert.Contains("($3)::bigint + $4", sql.CommandText);
        Assert.Equal(new object?[] { 1, 2 }, Assert.IsType<int[]>(sql.Parameters[0].Value).Cast<object?>());
        Assert.Equal(new object?[] { prefix, 1, 2 }, sql.Parameters.Skip(1).Select(p => p.Value));
    }

    [Fact]
    public void Filtered_include_rejects_unsupported_shapes_before_execution()
    {
        using var db = new NavigationIncludeDbContext();
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Include(o => o.Posts.Take(1)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Include(o => o.Posts.Select(post => post.Id)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Include(o => o.Posts.OrderBy(post => post.Id).Take(1).Where(post => post.Id > 0)));
        Assert.Throws<UnsupportedQueryExpressionException>(() => db.Owners.Include(o => o.Posts).Include(o => o.Posts.Where(post => post.Id > 0)));
    }
}

public sealed class NavigationIncludeDbContext : DbContext
{
    public NavigationIncludeDbContext() : base(_ => { }) { }
    public DbSet<IncludeOwner> Owners => Set<IncludeOwner>();
    public DbSet<IncludePost> Posts => Set<IncludePost>();
}

public sealed class IncludeOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [NotMapped] public List<IncludePost> Posts { get; set; } = [];
}

public sealed class IncludePost
{
    public int Id { get; set; }
    public int? IncludeOwnerId { get; set; }
    public string Title { get; set; } = "";
    [NotMapped] public IncludeOwner? IncludeOwner { get; set; }
}
