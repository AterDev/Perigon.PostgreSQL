using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class CorrelatedProjectionSqlTests
{
    [Fact]
    public void Dto_projection_supports_correlated_long_count()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Select(user => new
            {
                user.Id,
                BlogCount = db.Blogs.LongCount(blog => blog.RichUserId == user.Id)
            })
            .ToSql();

        Assert.Contains("SELECT e.\"id\" AS \"Id\", (SELECT count(*) FROM \"blogs\" AS s", sql.CommandText);
        Assert.Contains("s.\"rich_user_id\" IS NOT DISTINCT FROM e.\"id\"", sql.CommandText);
    }

    [Fact]
    public void Dto_projection_supports_correlated_count_with_integer_cast()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Select(user => new
            {
                user.Id,
                BlogCount = db.Blogs.Count(blog => blog.RichUserId == user.Id)
            })
            .ToSql();

        Assert.Contains("(SELECT count(*)::integer FROM \"blogs\" AS s", sql.CommandText);
    }

    [Fact]
    public void Dto_projection_supports_correlated_sum_with_empty_set_default()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Select(user => new
            {
                user.Id,
                BlogIdSum = db.Blogs.Sum(blog => blog.Id)
            })
            .ToSql();

        Assert.Contains("(SELECT COALESCE(sum(s.\"id\"), 0)::int FROM \"blogs\" AS s", sql.CommandText);
    }
}
