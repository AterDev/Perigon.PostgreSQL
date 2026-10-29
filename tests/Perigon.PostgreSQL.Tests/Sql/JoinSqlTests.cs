using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class JoinSqlTests
{
    [Fact]
    public void Correlated_any_generates_exists_subquery()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => db.Blogs.Any(b => b.RichUserId == u.Id && b.IsPublic))
            .ToSql();

        Assert.Contains("FROM \"rich_users\" AS e", sql.CommandText);
        Assert.Contains("EXISTS (SELECT 1 FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Contains("s.\"is_public\"", sql.CommandText);
    }

    [Fact]
    public void Correlated_any_preserves_subquery_parameter_order()
    {
        using var db = new TestDbContext();
        var expectedName = "public";

        var sql = db.RichUsers
            .Where(u => db.Blogs.Where(b => b.Name == expectedName).Any(b => b.RichUserId == u.Id))
            .ToSql();

        Assert.Contains("FROM \"rich_users\" AS e", sql.CommandText);
        Assert.Contains("s.\"name\" IS NOT DISTINCT FROM $1", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Single(sql.Parameters);
        Assert.Equal(expectedName, sql.Parameters[0].Value);
    }

    [Fact]
    public void Correlated_any_uses_distinct_from_for_nullable_inequality()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(user => db.Blogs.Any(blog => blog.Name != "blocked" && blog.RichUserId == user.Id))
            .ToSql();

        Assert.Contains("s.\"name\" IS DISTINCT FROM $1", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
    }

    [Fact]
    public void Captured_filtered_queryable_preserves_source_predicate()
    {
        using var db = new TestDbContext();
        var filtered = db.Blogs.Where(blog => blog.IsPublic);

        var sql = db.RichUsers
            .Where(user => filtered.Any(blog => blog.RichUserId == user.Id))
            .ToSql();

        Assert.Contains("s.\"is_public\"", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
    }

    [Fact]
    public void Captured_filtered_queryable_method_chain_preserves_all_predicates()
    {
        using var db = new TestDbContext();
        var filtered = db.Blogs.Where(blog => blog.IsPublic);

        var sql = db.RichUsers
            .Where(user => filtered.Where(blog => blog.Name.StartsWith("A"))
                .Any(blog => blog.RichUserId == user.Id))
            .ToSql();

        Assert.Contains("s.\"is_public\"", sql.CommandText);
        Assert.Contains("s.\"name\" LIKE $1", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Equal("A%", sql.Parameters[0].Value);
    }

    [Fact]
    public void Correlated_all_generates_not_exists_with_null_safe_predicate()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(user => db.Blogs.All(blog => blog.RichUserId != user.Id || blog.Name != null))
            .ToSql();

        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("IS NOT TRUE", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\"", sql.CommandText);
        Assert.Contains("s.\"name\" IS NOT NULL", sql.CommandText);
    }

    [Fact]
    public void Correlated_all_uses_distinct_from_for_nullable_inequality()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(user => db.Blogs.All(blog => blog.Name != "blocked"))
            .ToSql();

        Assert.Contains("s.\"name\" IS DISTINCT FROM $1", sql.CommandText);
        Assert.Contains("IS NOT TRUE", sql.CommandText);
        Assert.Equal("blocked", sql.Parameters[0].Value);
    }

    [Fact]
    public void Correlated_all_negation_preserves_nullable_boolean_result()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(user => db.Blogs.All(blog => !(blog.Name == "blocked")))
            .ToSql();

        Assert.Contains("NOT (COALESCE((s.\"name\" IS NOT DISTINCT FROM $1), FALSE))", sql.CommandText);
        Assert.Contains("IS NOT TRUE", sql.CommandText);
    }

    [Fact]
    public void Correlated_all_preserves_captured_source_filter()
    {
        using var db = new TestDbContext();
        var filtered = db.Blogs.Where(blog => blog.IsPublic);

        var sql = db.RichUsers
            .Where(user => filtered.All(blog => blog.RichUserId == user.Id))
            .ToSql();

        Assert.Contains("s.\"is_public\"", sql.CommandText);
        Assert.Contains("IS NOT TRUE", sql.CommandText);
    }

    [Fact]
    public void Correlated_all_rejects_paged_source()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(user => db.Blogs.Take(1).All(blog => blog.RichUserId == user.Id)).ToSql());

        Assert.Contains("All subqueries", error.Message);
    }

    [Fact]
    public void Unsupported_queryable_any_source_is_rejected()
    {
        using var db = new TestDbContext();
        var source = new[] { new Blog() }.AsQueryable();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(user => source.Any(blog => blog.RichUserId == user.Id)).ToSql());

        Assert.Contains("query source", error.Message);
    }

    [Fact]
    public void Nested_correlated_any_uses_distinct_aliases()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => db.Blogs.Any(b =>
                b.RichUserId == u.Id &&
                db.RichUsers.Any(inner => inner.Id == b.RichUserId)))
            .ToSql();

        Assert.Contains("FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("FROM \"rich_users\" AS ss", sql.CommandText);
        Assert.Contains("ss.\"id\" IS NOT DISTINCT FROM s.\"rich_user_id\"", sql.CommandText);
    }

    [Fact]
    public void Joins_page_outer_rows_before_matching_children()
    {
        using var db = new TestDbContext();

        var inner = db.RichUsers.Where(u => u.Status == "active").OrderBy(u => u.Id).Skip(2).Take(1)
            .Join(db.Blogs.Where(b => b.Name == "blog"), u => u.Id, b => b.RichUserId,
                (u, b) => new { u.UserName, b.Name }).ToSql();
        Assert.Contains("FROM (SELECT p.* FROM \"rich_users\" AS p WHERE (p.\"status\" = $1) ORDER BY p.\"id\" ASC LIMIT $2 OFFSET $3) AS o INNER JOIN", inner.CommandText);
        Assert.Equal(new object?[] { "active", 1, 2, "blog" }, inner.Parameters.Select(p => p.Value));

        var left = db.RichUsers.Skip(1).GroupJoin(db.Blogs, u => u.Id, b => b.RichUserId,
                (u, blogs) => new { u, blogs }).SelectMany(x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name }).ToSql();
        Assert.Contains("FROM (SELECT p.* FROM \"rich_users\" AS p OFFSET $1) AS o LEFT JOIN", left.CommandText);
    }

    [Fact]
    public void Paged_join_rejects_filters_after_paging()
    {
        using var db = new TestDbContext();
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Take(1).Where(u => u.IsActive).Join(db.Blogs, u => u.Id, b => b.RichUserId,
                (u, b) => new { u.UserName, b.Name }).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Take(5).Skip(1).Join(db.Blogs, u => u.Id, b => b.RichUserId,
                (u, b) => new { u.UserName, b.Name }).ToSql());
    }

    [Fact]
    public void Inner_join_with_projection_generates_join_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Join(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { UserName = u.UserName, BlogName = b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\"",
            sql.CommandText);
    }

    [Fact]
    public void Inner_join_preserves_outer_where_parameters()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Status == "active")
            .Join(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { u.Id, b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"id\" AS \"Id\", i.\"name\" AS \"Name\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE (o.\"status\" = $1)",
            sql.CommandText);
        Assert.Equal("active", sql.Parameters[0].Value);
    }

    [Fact]
    public void Inner_join_preserves_inner_where_parameters()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Join(
                db.Blogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { u.UserName, BlogName = b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE i.\"is_public\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupJoin_selectmany_default_if_empty_generates_left_join_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupJoin(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\"",
            sql.CommandText);
    }

    [Fact]
    public void Left_join_preserves_outer_where()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .GroupJoin(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE o.\"is_active\"",
            sql.CommandText);
    }

    [Fact]
    public void Left_join_places_inner_where_in_join_condition()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupJoin(
                db.Blogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" AND i.\"is_public\"",
            sql.CommandText);
    }

    [Fact]
    public void Inner_join_preserves_outer_ordering_after_join()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .OrderBy(u => u.UserName)
            .Join(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { u.UserName, b.Name })
            .ToSql();

        Assert.Contains("ORDER BY o.\"user_name\" ASC", sql.CommandText);
    }

    [Fact]
    public void Left_join_rejects_paging_or_ordering_on_join_sources()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .GroupJoin(
                    db.Blogs.OrderBy(b => b.Name).Take(1),
                    u => u.Id,
                    b => b.RichUserId,
                    (u, blogs) => new { u, blogs })
                .SelectMany(
                    x => x.blogs.DefaultIfEmpty(),
                    (x, b) => new { x.u.UserName, BlogName = b!.Name })
                .ToSql());

        Assert.Contains("inner", error.Message);
        Assert.Contains("Take", error.Message);
    }
}
