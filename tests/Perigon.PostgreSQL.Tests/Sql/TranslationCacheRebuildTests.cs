using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class TranslationCacheRebuildTests
{
    [Fact]
    public void Rebuilding_same_shape_does_not_reuse_the_previous_closure_value()
    {
        using var db = new TestDbContext();

        var first = BuildAgeQuery(db, 20).ToQuerySql();
        var second = BuildAgeQuery(db, 42).ToQuerySql();

        Assert.Equal(first.CommandText, second.CommandText);
        Assert.Equal([20], first.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([42], second.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Rebuilding_same_shape_preserves_null_semantics_per_query()
    {
        using var db = new TestDbContext();

        var nullQuery = BuildStatusQuery(db, null).ToQuerySql();
        var valueQuery = BuildStatusQuery(db, "active").ToQuerySql();
        var nullAgainQuery = BuildStatusQuery(db, null).ToQuerySql();

        Assert.Contains("IS NULL", nullQuery.CommandText);
        Assert.Empty(nullQuery.Parameters);
        Assert.Contains("= $1", valueQuery.CommandText);
        Assert.Equal(["active"], valueQuery.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Contains("IS NULL", nullAgainQuery.CommandText);
        Assert.Empty(nullAgainQuery.Parameters);
    }

    [Fact]
    public void Rebuilding_queries_concurrently_keeps_each_query_parameter_bound()
    {
        using var db = new TestDbContext();
        var queries = Enumerable.Range(0, 32)
            .Select(index => BuildAgeQuery(db, index))
            .ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<(int Value, string Sql)>();

        Parallel.ForEach(queries, query =>
        {
            var sql = query.ToQuerySql();
            results.Add(((int)sql.Parameters.Single().Value!, sql.CommandText));
        });

        Assert.Equal(32, results.Count);
        Assert.Equal(Enumerable.Range(0, 32), results.Select(result => result.Value).OrderBy(value => value));
        Assert.Single(results.Select(result => result.Sql).Distinct());
    }

    private static IQueryable<RichUser> BuildAgeQuery(TestDbContext db, int minimumAge)
    {
        return db.RichUsers.Where(user => user.Age >= minimumAge);
    }

    private static IQueryable<RichUser> BuildStatusQuery(TestDbContext db, string? status)
    {
        return db.RichUsers.Where(user => user.Status == status);
    }
}
