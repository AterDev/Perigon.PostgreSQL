using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class ArraySqlTests
{
    [Fact]
    public void ContainsAll_translates_to_contains_operator()
    {
        using var db = new TestDbContext();
        var tags = new[] { "postgres", "aot" };

        var sql = db.RichUsers.Where(u => u.Tags!.ContainsAll(tags)).ToQuerySql();

        Assert.Contains("e.\"tags\" @> $1", sql.CommandText);
        Assert.Same(tags, sql.Parameters[0].Value);
    }

    [Fact]
    public void IsContainedBy_translates_to_contained_by_operator()
    {
        using var db = new TestDbContext();
        var tags = new[] { "postgres", "aot", "orm" };

        var sql = db.RichUsers.Where(u => u.Tags!.IsContainedBy(tags)).ToQuerySql();

        Assert.Contains("e.\"tags\" <@ $1", sql.CommandText);
    }

    [Fact]
    public void Length_translates_to_cardinality()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.Tags!.Length > 0).ToQuerySql();

        Assert.Contains("cardinality(e.\"tags\") > $1", sql.CommandText);
        Assert.Equal(0, sql.Parameters[0].Value);
    }

    [Fact]
    public void Empty_parameter_array_is_still_parameterized()
    {
        using var db = new TestDbContext();
        var ids = Array.Empty<int>();

        var sql = db.RichUsers.Where(u => ids.Contains(u.Id)).ToQuerySql();

        Assert.Contains("e.\"id\" = ANY($1)", sql.CommandText);
        Assert.Same(ids, sql.Parameters[0].Value);
    }

    [Fact]
    public void Array_any_without_predicate_checks_cardinality()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.Tags!.Any()).ToQuerySql();

        Assert.Contains("cardinality(e.\"tags\") > 0", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Array_any_equality_predicate_translates_to_any_operator()
    {
        using var db = new TestDbContext();
        var tag = "postgres";

        var sql = db.RichUsers.Where(u => u.Tags!.Any(t => t == tag)).ToQuerySql();

        Assert.Contains("$1 = ANY(e.\"tags\")", sql.CommandText);
        Assert.Equal(tag, sql.Parameters[0].Value);
    }

    [Fact]
    public void Array_all_equality_predicate_translates_to_not_exists_unnest()
    {
        using var db = new TestDbContext();
        var tag = "postgres";

        var sql = db.RichUsers.Where(u => u.Tags!.All(t => t == tag)).ToQuerySql();

        Assert.Contains("e.\"tags\" IS NOT NULL", sql.CommandText);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM unnest(e.\"tags\") AS p(\"value\") WHERE p.\"value\" IS DISTINCT FROM $1)", sql.CommandText);
        Assert.Equal(tag, sql.Parameters[0].Value);
    }
}
