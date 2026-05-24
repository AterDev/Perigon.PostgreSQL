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

    private sealed class UserSummary
    {
        public int Id { get; init; }

        public string Name { get; init; } = "";
    }
}
