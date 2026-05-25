using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class GroupBySqlTests
{
    [Fact]
    public void GroupBy_count_projection_generates_group_by_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_preserves_source_where()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Age > 18)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\" FROM \"rich_users\" AS e WHERE (e.\"age\" > $1) GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_sum_min_max_average_projection_generates_aggregate_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new
            {
                Status = g.Key,
                TotalAge = g.Sum(u => u.Age),
                MinAge = g.Min(u => u.Age),
                MaxAge = g.Max(u => u.Age),
                AvgAge = g.Average(u => u.Age)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", sum(e.\"age\") AS \"TotalAge\", min(e.\"age\") AS \"MinAge\", max(e.\"age\") AS \"MaxAge\", avg(e.\"age\") AS \"AvgAge\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_long_count_uses_count_star()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_projection_where_order_and_take_wraps_aggregate_query()
    {
        using var db = new TestDbContext();
        var minCount = 2;

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), AverageAge = g.Average(u => u.Age) })
            .Where(x => x.Count >= minCount)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Status)
            .Take(5)
            .ToSql();

        Assert.Equal(
            "SELECT * FROM (SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\", avg(e.\"age\") AS \"AverageAge\" FROM \"rich_users\" AS e WHERE e.\"is_active\" GROUP BY e.\"status\") AS g WHERE (g.\"Count\" >= $1) ORDER BY g.\"Count\" DESC, g.\"Status\" ASC LIMIT $2",
            sql.CommandText);
        Assert.Equal(2, sql.Parameters.Count);
        Assert.Equal(minCount, sql.Parameters[0].Value);
        Assert.Equal(5, sql.Parameters[1].Value);
    }

    [Fact]
    public void GroupBy_projection_where_generates_having_sql()
    {
        using var db = new TestDbContext();
        var minCount = 2;

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .Where(x => x.Count >= minCount && x.Status != null)
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\" FROM \"rich_users\" AS e WHERE e.\"is_active\" GROUP BY e.\"status\" HAVING ((count(*) >= $1) AND e.\"status\" IS NOT NULL)",
            sql.CommandText);
        Assert.Single(sql.Parameters);
        Assert.Equal(minCount, sql.Parameters[0].Value);
    }

    [Fact]
    public void GroupBy_projection_where_preserves_source_parameters()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Age > 18)
            .GroupBy(u => u.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .Where(x => x.Count > 1 && x.Status != null)
            .Skip(1)
            .ToSql();

        Assert.Equal(
            "SELECT * FROM (SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\" FROM \"rich_users\" AS e WHERE (e.\"age\" > $1) GROUP BY e.\"status\") AS g WHERE ((g.\"Count\" > $2) AND g.\"Status\" IS NOT NULL) OFFSET $3",
            sql.CommandText);
        Assert.Equal(3, sql.Parameters.Count);
        Assert.Equal(18, sql.Parameters[0].Value);
        Assert.Equal(1, sql.Parameters[1].Value);
        Assert.Equal(1, sql.Parameters[2].Value);
    }

    [Fact]
    public void GroupBy_multiple_keys_generates_multi_column_group_by()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => new { u.Status, u.IsActive })
            .Select(g => new
            {
                g.Key.Status,
                g.Key.IsActive,
                Count = g.Count(),
                AverageAge = g.Average(u => u.Age)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", e.\"is_active\" AS \"IsActive\", count(*) AS \"Count\", avg(e.\"age\") AS \"AverageAge\" FROM \"rich_users\" AS e GROUP BY e.\"status\", e.\"is_active\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_member_init_projection_generates_aggregate_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new StatusAggregateRow
            {
                Status = g.Key,
                Count = g.LongCount(),
                AverageAge = g.Average(u => u.Age)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(*) AS \"Count\", avg(e.\"age\") AS \"AverageAge\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_count_distinct_generates_count_distinct_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new
            {
                Status = g.Key,
                DistinctNames = g.CountDistinct(u => u.UserName),
                DistinctAges = g.LongCountDistinct(u => u.Age)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", count(distinct e.\"user_name\") AS \"DistinctNames\", count(distinct e.\"age\") AS \"DistinctAges\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_array_agg_generates_array_agg_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new
            {
                Status = g.Key,
                Names = g.ArrayAgg(u => u.UserName)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", array_agg(e.\"user_name\") AS \"Names\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupBy_jsonb_agg_generates_jsonb_agg_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupBy(u => u.Status)
            .Select(g => new
            {
                Status = g.Key,
                Profiles = g.JsonbAgg(u => u.ProfileJson)
            })
            .ToSql();

        Assert.Equal(
            "SELECT e.\"status\" AS \"Status\", jsonb_agg(e.\"profile_json\")::text AS \"Profiles\" FROM \"rich_users\" AS e GROUP BY e.\"status\"",
            sql.CommandText);
    }

    private sealed class StatusAggregateRow
    {
        public string? Status { get; set; }

        public long Count { get; set; }

        public double AverageAge { get; set; }
    }
}
