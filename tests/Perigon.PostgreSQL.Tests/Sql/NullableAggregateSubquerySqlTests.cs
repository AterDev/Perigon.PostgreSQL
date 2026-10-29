using Perigon.PostgreSQL;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class NullableAggregateSubquerySqlTests
{
    [Fact]
    public void Nullable_min_max_average_generate_scalar_subqueries()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Select(user => new
            {
                user.Id,
                Min = db.Blogs.Min(blog => (int?)blog.Id),
                Max = db.Blogs.Max(blog => (int?)blog.Id),
                Average = db.Blogs.Average(blog => (double?)blog.Id)
            })
            .ToSql();

        Assert.Contains("(SELECT min(s.\"id\") FROM \"blogs\" AS s)", sql.CommandText);
        Assert.Contains("(SELECT max(s.\"id\") FROM \"blogs\" AS s)", sql.CommandText);
        Assert.Contains("(SELECT avg(s.\"id\")::double precision FROM \"blogs\" AS s)", sql.CommandText);
    }

    [Fact]
    public void Non_nullable_projection_aggregate_is_marked_for_runtime_empty_check()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Select(user => new { Value = db.Blogs.Min(blog => blog.Id) }).ToSql();

        Assert.Contains("(SELECT min(s.\"id\") FROM \"blogs\" AS s)", sql.CommandText);
    }

    [Fact]
    public void Non_nullable_aggregate_in_where_is_rejected()
    {
        using var db = new TestDbContext();

        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(user => db.Blogs.Min(blog => blog.Id) > 0).ToSql());
    }

    [Fact]
    public void Bool_and_guid_min_max_are_rejected()
    {
        using var db = new SpecialAggregateDbContext();

        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.Rows.Select(row => new { Value = db.Rows.Min(item => item.NullableFlag) }).ToSql());
        Assert.Throws<Perigon.PostgreSQL.Expressions.UnsupportedQueryExpressionException>(() =>
            db.Rows.Select(row => new { Value = db.Rows.Max(item => item.NullableGuid) }).ToSql());
    }
}

public sealed class SpecialAggregateDbContext : DbContext
{
    public DbSet<SpecialAggregateRow> Rows => Set<SpecialAggregateRow>();
}

[Table("special_aggregate_rows")]
public sealed class SpecialAggregateRow
{
    public int Id { get; set; }

    public bool? NullableFlag { get; set; }

    public Guid? NullableGuid { get; set; }
}
