using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class SubquerySqlTests
{
    [Fact]
    public void Correlated_long_count_generates_scalar_count_subquery()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(user => db.Blogs.LongCount(blog => blog.RichUserId == user.Id) > 0)
            .ToSql();

        Assert.Contains("(SELECT count(*) FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
    }

    [Fact]
    public void Captured_long_count_query_preserves_source_filter_and_parameters()
    {
        using var db = new TestDbContext();
        var filtered = db.Blogs.Where(blog => blog.Name == "public");

        var sql = db.RichUsers
            .Where(user => filtered.LongCount(blog => blog.RichUserId == user.Id) > 0)
            .ToSql();

        Assert.Contains("s.\"name\" IS NOT DISTINCT FROM $1", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Equal("public", sql.Parameters[0].Value);
    }

    [Fact]
    public void Long_count_rejects_paged_subquery()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(user => db.Blogs.Take(1).LongCount(blog => blog.RichUserId == user.Id) > 0).ToSql());

        Assert.Contains("LongCount subqueries", error.Message);
    }
}
