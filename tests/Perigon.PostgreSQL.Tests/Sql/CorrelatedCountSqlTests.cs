using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class CorrelatedCountSqlTests
{
    [Fact]
    public void Long_count_translates_captured_filter_and_correlated_predicate()
    {
        using var db = new TestDbContext();
        var name = "selected";
        var filtered = db.Blogs.Where(b => b.Name == name);
        var sql = db.RichUsers.Where(u => filtered.LongCount(b => b.RichUserId == u.Id) > 1L).ToSql();
        Assert.Contains("(SELECT count(*) FROM \"blogs\" AS s WHERE", sql.CommandText);
        Assert.Contains("s.\"name\" IS NOT DISTINCT FROM $1", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
        Assert.Equal(new object?[] { name, 1L }, sql.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void Count_translates_to_integer_cast_for_where_subquery()
    {
        using var db = new TestDbContext();
        var sql = db.RichUsers.Where(u => db.Blogs.Count(b => b.RichUserId == u.Id) > 0).ToSql();
        Assert.Contains("(SELECT count(*)::integer FROM \"blogs\" AS s", sql.CommandText);
    }

    [Fact]
    public void Long_count_rejects_paging_before_the_subquery()
    {
        using var db = new TestDbContext();
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => db.Blogs.Take(1).LongCount(b => b.RichUserId == u.Id) > 0).ToSql());
    }

    [Fact]
    public void Nested_count_uses_distinct_aliases()
    {
        using var db = new TestDbContext();
        var sql = db.RichUsers.Where(u => db.Blogs.LongCount(b => b.RichUserId == u.Id &&
            db.RichUsers.LongCount(v => v.Id == b.RichUserId) > 0L) > 0L).ToSql();
        Assert.Contains("FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("FROM \"rich_users\" AS ss", sql.CommandText);
        Assert.Contains("ss.\"id\" IS NOT DISTINCT FROM s.\"rich_user_id\"", sql.CommandText);
    }
}
