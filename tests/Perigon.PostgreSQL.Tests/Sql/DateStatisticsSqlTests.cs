using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class DateStatisticsSqlTests
{
    [Fact]
    public void Daily_statistics_grouping_uses_date_trunc_day()
    {
        using var db = new TestDbContext();
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = db.StatisticOrders
            .Where(o => o.OrderTime > cutoff)
            .GroupBy(o => o.OrderTime.Date)
            .Select(g => new SummaryLineChartRow
            {
                Date = g.Key,
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Equal(
            "SELECT date_trunc('day', e.\"order_time\") AS \"Date\", count(*) AS \"Count\", sum(e.\"total_price\") AS \"TotalAmount\" FROM \"statistic_orders\" AS e WHERE (e.\"order_time\" > $1) GROUP BY date_trunc('day', e.\"order_time\")",
            sql.CommandText);
        Assert.Single(sql.Parameters);
        Assert.Equal(cutoff, sql.Parameters[0].Value);
    }

    [Fact]
    public void Monthly_statistics_grouping_uses_extract_and_make_timestamp()
    {
        using var db = new TestDbContext();

        var sql = db.StatisticOrders
            .GroupBy(o => new { o.OrderTime.Year, o.OrderTime.Month })
            .Select(g => new SummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, g.Key.Month, 1),
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Equal(
            "SELECT make_timestamp(extract(year from e.\"order_time\")::int, extract(month from e.\"order_time\")::int, 1, 0, 0, 0) AS \"Date\", count(*) AS \"Count\", sum(e.\"total_price\") AS \"TotalAmount\" FROM \"statistic_orders\" AS e GROUP BY extract(year from e.\"order_time\")::int, extract(month from e.\"order_time\")::int",
            sql.CommandText);
    }

    [Fact]
    public void Quarterly_statistics_grouping_supports_arithmetic_key_projection()
    {
        using var db = new TestDbContext();

        var sql = db.StatisticOrders
            .GroupBy(o => new { o.OrderTime.Year, Quarter = (o.OrderTime.Month - 1) / 3 + 1 })
            .Select(g => new SummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1),
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Contains("SELECT make_timestamp(extract(year from e.\"order_time\")::int,", sql.CommandText);
        Assert.Contains("count(*) AS \"Count\"", sql.CommandText);
        Assert.Contains("sum(e.\"total_price\") AS \"TotalAmount\"", sql.CommandText);
        Assert.Contains("GROUP BY extract(year from e.\"order_time\")::int,", sql.CommandText);
        Assert.Contains("extract(month from e.\"order_time\")::int", sql.CommandText);
    }

    [Fact]
    public void Grouped_statistics_projection_supports_string_filter_and_ordering()
    {
        using var db = new TestDbContext();

        var sql = db.StatisticOrders
            .GroupBy(o => new { o.OrderTime.Year, o.Status })
            .Select(g => new SummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, 1, 1),
                Status = g.Key.Status,
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .Where(x => x.Status!.StartsWith("paid") && x.Count > 1)
            .OrderBy(x => x.Status)
            .ToSql();

        Assert.Contains("SELECT * FROM (SELECT make_timestamp(extract(year from e.\"order_time\")::int, 1, 1, 0, 0, 0) AS \"Date\"", sql.CommandText);
        Assert.Contains("e.\"status\" AS \"Status\"", sql.CommandText);
        Assert.Contains("WHERE (g.\"Status\" LIKE $1 AND (g.\"Count\" > $2))", sql.CommandText);
        Assert.Contains("ORDER BY g.\"Status\" ASC", sql.CommandText);
        Assert.Equal(["paid%", 1], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void DateTimeOffset_daily_statistics_grouping_uses_date_trunc_day()
    {
        using var db = new TestDbContext();
        var cutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var sql = db.StatisticOffsetOrders
            .Where(o => o.OrderTime > cutoff)
            .GroupBy(o => o.OrderTime.Date)
            .Select(g => new SummaryLineChartOffsetRow
            {
                Date = g.Key,
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Equal(
            "SELECT date_trunc('day', e.\"order_time\") AS \"Date\", count(*) AS \"Count\", sum(e.\"total_price\") AS \"TotalAmount\" FROM \"statistic_offset_orders\" AS e WHERE (e.\"order_time\" > $1) GROUP BY date_trunc('day', e.\"order_time\")",
            sql.CommandText);
        Assert.Single(sql.Parameters);
        Assert.Equal(cutoff, sql.Parameters[0].Value);
    }

    [Fact]
    public void DateTimeOffset_monthly_statistics_grouping_uses_make_timestamptz()
    {
        using var db = new TestDbContext();

        var sql = db.StatisticOffsetOrders
            .GroupBy(o => new { o.OrderTime.Year, o.OrderTime.Month })
            .Select(g => new SummaryLineChartOffsetRow
            {
                Date = new DateTimeOffset(g.Key.Year, g.Key.Month, 1, 0, 0, 0, TimeSpan.Zero),
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Equal(
            "SELECT make_timestamptz(extract(year from e.\"order_time\")::int, extract(month from e.\"order_time\")::int, 1, 0, 0, 0, 'UTC') AS \"Date\", count(*) AS \"Count\", sum(e.\"total_price\") AS \"TotalAmount\" FROM \"statistic_offset_orders\" AS e GROUP BY extract(year from e.\"order_time\")::int, extract(month from e.\"order_time\")::int",
            sql.CommandText);
    }

    [Fact]
    public void DateTimeOffset_quarterly_statistics_grouping_supports_arithmetic_key_projection()
    {
        using var db = new TestDbContext();

        var sql = db.StatisticOffsetOrders
            .GroupBy(o => new { o.OrderTime.Year, Quarter = (o.OrderTime.Month - 1) / 3 + 1 })
            .Select(g => new SummaryLineChartOffsetRow
            {
                Date = new DateTimeOffset(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero),
                Count = g.Count(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .ToSql();

        Assert.Contains("SELECT make_timestamptz(extract(year from e.\"order_time\")::int,", sql.CommandText);
        Assert.Contains("'UTC'", sql.CommandText);
        Assert.Contains("count(*) AS \"Count\"", sql.CommandText);
        Assert.Contains("sum(e.\"total_price\") AS \"TotalAmount\"", sql.CommandText);
        Assert.Contains("GROUP BY extract(year from e.\"order_time\")::int,", sql.CommandText);
        Assert.Contains("extract(month from e.\"order_time\")::int", sql.CommandText);
    }
}