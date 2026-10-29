using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class ProjectionSqlTests
{
    [Fact]
    public void Anonymous_projection_selects_only_requested_columns()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Age > 18)
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, u.UserName })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"id\" AS \"Id\", e.\"user_name\" AS \"UserName\" FROM \"rich_users\" AS e WHERE (e.\"age\" > $1) ORDER BY e.\"user_name\" ASC",
            sql.CommandText);
        Assert.Equal(18, sql.Parameters[0].Value);
    }

    [Fact]
    public void Dto_member_init_projection_uses_binding_names()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .Select(u => new UserSummary { Id = u.Id, Name = u.UserName })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"id\" AS \"Id\", e.\"user_name\" AS \"Name\" FROM \"rich_users\" AS e WHERE e.\"is_active\"",
            sql.CommandText);
    }

    [Fact]
    public void Scalar_member_projection_is_supported()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Select(u => u.UserName).ToSql();

        Assert.Equal("SELECT e.\"user_name\" AS \"UserName\" FROM \"rich_users\" AS e", sql.CommandText);
    }

    [Fact]
    public void Distinct_projection_generates_select_distinct()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .Select(u => u.Status)
            .Distinct()
            .ToSql();

        Assert.Equal("SELECT DISTINCT e.\"status\" AS \"Status\" FROM \"rich_users\" AS e WHERE e.\"is_active\"", sql.CommandText);
    }

    [Fact]
    public void Projection_supports_post_filter_order_and_paging()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.UserName })
            .Where(u => u.Id > 10)
            .OrderByDescending(u => u.UserName)
            .Skip(2)
            .Take(3)
            .ToSql();

        Assert.Equal(
            "SELECT * FROM (SELECT e.\"id\" AS \"Id\", e.\"user_name\" AS \"UserName\" FROM \"rich_users\" AS e WHERE e.\"is_active\") AS p WHERE (p.\"Id\" > $1) ORDER BY p.\"UserName\" DESC LIMIT $2 OFFSET $3",
            sql.CommandText);
        Assert.Equal([10, 3, 2], sql.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Projection_post_filter_preserves_inner_parameter_order()
    {
        using var db = new TestDbContext();
        var status = "active";

        var sql = db.RichUsers
            .Where(u => u.Status == status)
            .Select(u => new UserSummary { Id = u.Id, Name = u.UserName })
            .Where(u => u.Id > 10)
            .ToSql();

        Assert.Equal(
            "SELECT * FROM (SELECT e.\"id\" AS \"Id\", e.\"user_name\" AS \"Name\" FROM \"rich_users\" AS e WHERE (e.\"status\" = $1)) AS p WHERE (p.\"Id\" > $2)",
            sql.CommandText);
        Assert.Equal([status, 10], sql.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Distinct_projection_applies_before_post_filter_order_and_paging()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Select(u => new { u.Id, u.UserName })
            .Distinct()
            .Where(u => u.Id > 10)
            .OrderBy(u => u.UserName)
            .Skip(1)
            .Take(2)
            .ToSql();

        Assert.Equal(
            "SELECT * FROM (SELECT DISTINCT e.\"id\" AS \"Id\", e.\"user_name\" AS \"UserName\" FROM \"rich_users\" AS e) AS p WHERE (p.\"Id\" > $1) ORDER BY p.\"UserName\" ASC LIMIT $2 OFFSET $3",
            sql.CommandText);
        Assert.Equal([10, 2, 1], sql.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Distinct_after_projection_paging_is_rejected()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .Select(u => new { u.Id, u.UserName })
                .Take(1)
                .Distinct()
                .ToSql());

        Assert.Contains("Distinct after Skip or Take", error.Message);
    }

    [Fact]
    public void Distinct_after_source_ordering_is_rejected_instead_of_emitting_invalid_postgres_sql()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .OrderBy(u => u.Age)
                .Select(u => u.Status)
                .Distinct()
                .ToSql());

        Assert.Contains("Distinct over an ordered query", error.Message);
    }

    [Fact]
    public void Projection_post_filter_rejects_client_method_calls()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .Select(u => new UserSummary { Id = u.Id, Name = u.UserName })
                .Where(u => IsAdult(u.Id))
                .ToSql());

        Assert.Contains(nameof(IsAdult), error.Message);
    }

    [Fact]
    public void Projection_rejects_filter_after_take()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .Select(u => new UserSummary { Id = u.Id, Name = u.UserName })
                .Take(1)
                .Where(u => u.Id > 0)
                .ToSql());

        Assert.Contains("Where after Skip or Take", error.Message);
    }

    [Fact]
    public void Projection_rejects_reordered_or_duplicate_paging_and_orderby()
    {
        using var db = new TestDbContext();
        var query = db.RichUsers.Select(u => new UserSummary { Id = u.Id, Name = u.UserName });

        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            query.Take(2).Skip(1).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            query.Skip(1).Skip(1).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            query.Take(2).Take(1).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            query.OrderBy(u => u.Id).OrderBy(u => u.Name).ToSql());
    }

    private sealed class UserSummary
    {
        public int Id { get; init; }

        public string Name { get; init; } = "";
    }

    private static bool IsAdult(int id) => id > 18;
}
