using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class SelectSqlTests
{
    [Fact]
    public void Where_order_take_generates_parameterized_sql()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers
            .Where(u => u.Age > 18 && u.Status == "active")
            .OrderBy(u => u.UserName)
            .Take(10)
            .ToQuerySql();

        Assert.Equal(
            "SELECT e.\"id\", e.\"user_name\", e.\"age\", e.\"status\", e.\"tags\" FROM \"convention_users\" AS e WHERE ((e.\"age\" > $1) AND (e.\"status\" = $2)) ORDER BY e.\"user_name\" ASC LIMIT $3",
            sql.CommandText);
        Assert.Equal([18, "active", 10], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Null_comparison_uses_is_null()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers
            .Where(u => u.Status == null)
            .ToQuerySql();

        Assert.Contains("e.\"status\" IS NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Parameter_collection_contains_translates_to_any()
    {
        using var db = new TestDbContext();
        var ids = new[] { 1, 2, 3 };

        var sql = db.ConventionUsers
            .Where(u => ids.Contains(u.Id))
            .ToQuerySql();

        Assert.Contains("e.\"id\" = ANY($1)", sql.CommandText);
        Assert.Same(ids, sql.Parameters[0].Value);
    }

    [Fact]
    public void Array_column_contains_translates_to_array_contains()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers
            .Where(u => u.Tags!.Contains("developer"))
            .ToQuerySql();

        Assert.Contains("e.\"tags\" @> ARRAY[$1]", sql.CommandText);
        Assert.Equal("developer", sql.Parameters[0].Value);
    }

    [Fact]
    public void Array_overlap_extension_translates_to_overlap_operator()
    {
        using var db = new TestDbContext();
        var tags = new[] { "postgres", "aot" };

        var sql = db.ConventionUsers
            .Where(u => u.Tags!.Overlaps(tags))
            .ToQuerySql();

        Assert.Contains("e.\"tags\" && $1", sql.CommandText);
        Assert.Same(tags, sql.Parameters[0].Value);
    }
}
