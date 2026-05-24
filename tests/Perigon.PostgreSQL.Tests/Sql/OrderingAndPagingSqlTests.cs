using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class OrderingAndPagingSqlTests
{
    [Fact]
    public void OrderByDescending_generates_desc()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.OrderByDescending(u => u.CreatedAt).ToQuerySql();

        Assert.Contains("ORDER BY e.\"created_at\" DESC", sql.CommandText);
    }

    [Fact]
    public void ThenBy_generates_multiple_orderings()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .OrderBy(u => u.Status)
            .ThenBy(u => u.UserName)
            .ToQuerySql();

        Assert.Contains("ORDER BY e.\"status\" ASC, e.\"user_name\" ASC", sql.CommandText);
    }

    [Fact]
    public void ThenByDescending_generates_multiple_orderings()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .OrderBy(u => u.Status)
            .ThenByDescending(u => u.CreatedAt)
            .ToQuerySql();

        Assert.Contains("ORDER BY e.\"status\" ASC, e.\"created_at\" DESC", sql.CommandText);
    }

    [Fact]
    public void Captured_skip_and_take_values_are_supported()
    {
        using var db = new TestDbContext();
        var skip = 20;
        var take = 10;

        var sql = db.RichUsers.OrderBy(u => u.Id).Skip(skip).Take(take).ToQuerySql();

        Assert.EndsWith("ORDER BY e.\"id\" ASC LIMIT $1 OFFSET $2", sql.CommandText);
        Assert.Equal([take, skip], sql.Parameters.Select(p => p.Value).ToArray());
    }
}
