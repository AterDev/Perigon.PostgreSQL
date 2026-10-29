using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class AggregateSqlTests
{
    [Fact]
    public void Count_sql_uses_count_star_without_loading_columns()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.Age > 18).ToCountSql();

        Assert.Equal("SELECT count(*) FROM \"rich_users\" AS e WHERE (e.\"age\" > $1)", sql.CommandText);
        Assert.Equal(18, sql.Parameters[0].Value);
    }

    [Fact]
    public void Any_sql_uses_select_one_limit_one()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.Status == "active").ToAnySql();

        Assert.Equal("SELECT 1 FROM \"rich_users\" AS e WHERE (e.\"status\" = $1) LIMIT 1", sql.CommandText);
        Assert.Equal("active", sql.Parameters[0].Value);
    }

    [Fact]
    public void Count_without_where_has_no_where_clause()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.ToCountSql();

        Assert.Equal("SELECT count(*) FROM \"rich_users\" AS e", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Long_count_sql_reuses_count_star()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.IsActive).ToLongCountSql();

        Assert.Equal("SELECT count(*) FROM \"rich_users\" AS e WHERE e.\"is_active\"", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Any_without_where_has_no_where_clause()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.ToAnySql();

        Assert.Equal("SELECT 1 FROM \"rich_users\" AS e LIMIT 1", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Count_with_order_and_take_counts_only_the_paged_source()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Age > 18)
            .OrderBy(u => u.Age)
            .Take(3)
            .ToCountSql();

        Assert.Equal(
            "SELECT count(*) FROM (SELECT 1 FROM \"rich_users\" AS e WHERE (e.\"age\" > $1) ORDER BY e.\"age\" ASC LIMIT $2) AS q",
            sql.CommandText);
        Assert.Equal([18, 3], sql.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Any_with_skip_uses_a_paged_subquery_before_outer_limit()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .OrderByDescending(u => u.Id)
            .Skip(4)
            .ToAnySql();

        Assert.Equal(
            "SELECT 1 FROM (SELECT 1 FROM \"rich_users\" AS e ORDER BY e.\"id\" DESC OFFSET $1) AS q LIMIT 1",
            sql.CommandText);
        Assert.Equal(4, sql.Parameters[0].Value);
    }

    [Fact]
    public void Entity_queries_reject_reordered_paging_and_filtering()
    {
        using var db = new TestDbContext();

        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Take(2).Skip(1).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Skip(1).Skip(1).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Take(2).Where(u => u.Id > 0).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.OrderBy(u => u.Id).OrderBy(u => u.Age).ToSql());
    }
}
