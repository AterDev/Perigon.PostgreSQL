using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class RebuiltQueryTemplateCacheTests
{
    [Fact]
    public void Hot_shape_rebuild_rebinds_age_and_limit_without_stale_values()
    {
        using var db = new TestDbContext();

        var first = BuildDescendingLimitFive(db, 20).ToQuerySql();
        var second = BuildDescendingLimitFive(db, 42).ToQuerySql();
        var differentLimit = BuildDescendingLimitTen(db, 42).ToQuerySql();

        Assert.Equal(first.CommandText, second.CommandText);
        Assert.Equal(first.CommandText, differentLimit.CommandText);
        Assert.Equal([20, 5], first.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([42, 5], second.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([42, 10], differentLimit.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Hot_shape_cache_keeps_comparison_operator_in_the_shape()
    {
        using var db = new TestDbContext();

        var greaterThan = BuildGreaterThan(db, 20).ToQuerySql();
        var equal = BuildEqual(db, 20).ToQuerySql();

        Assert.Contains("e.\"age\" > $1", greaterThan.CommandText);
        Assert.Contains("e.\"age\" = $1", equal.CommandText);
        Assert.Equal([20, 5], greaterThan.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([20, 5], equal.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Hot_shape_cache_keeps_ordering_direction_in_the_shape()
    {
        using var db = new TestDbContext();

        var descending = BuildDescendingLimitFive(db, 20).ToQuerySql();
        var ascending = BuildAscendingLimitFive(db, 20).ToQuerySql();

        Assert.Contains("ORDER BY e.\"created_at\" DESC", descending.CommandText);
        Assert.Contains("ORDER BY e.\"created_at\" ASC", ascending.CommandText);
        Assert.NotEqual(descending.CommandText, ascending.CommandText);
    }

    [Fact]
    public void Adjacent_shape_without_boolean_predicate_keeps_general_translation_behavior()
    {
        using var db = new TestDbContext();

        var minimumAge = 33;
        var sql = db.RichUsers
            .Where(user => user.Age >= minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(5)
            .ToQuerySql();

        Assert.Contains("WHERE (e.\"age\" >= $1)", sql.CommandText);
        Assert.Contains("ORDER BY e.\"created_at\" DESC", sql.CommandText);
        Assert.Equal([minimumAge, 5], sql.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Rebuilt_single_comparison_separates_optional_ordering_and_limit()
    {
        using var db = new TestDbContext();
        static IQueryable<RichUser> Filter(TestDbContext context, int age) =>
            context.RichUsers.Where(user => user.Age >= age);

        var first = Filter(db, 10).ToQuerySql();
        var second = Filter(db, 50).ToQuerySql();
        var ordered = Filter(db, 50).OrderBy(user => user.CreatedAt).ToQuerySql();
        var limited = Filter(db, 50).Take(4).ToQuerySql();

        Assert.Equal(first.CommandText, second.CommandText);
        Assert.Equal([50], second.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Contains("ORDER BY e.\"created_at\" ASC", ordered.CommandText);
        Assert.DoesNotContain("ORDER BY", limited.CommandText);
        Assert.EndsWith("LIMIT $2", limited.CommandText);
        Assert.Equal([50, 4], limited.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void Rebuilding_hot_shape_queries_concurrently_keeps_parameters_bound()
    {
        using var db = new TestDbContext();
        var queries = Enumerable.Range(0, 32)
            .Select(index => BuildDescendingLimitFive(db, index))
            .ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<(int Age, int Limit, string Sql)>();

        Parallel.ForEach(queries, query =>
        {
            var sql = query.ToQuerySql();
            results.Add(((int)sql.Parameters[0].Value!, (int)sql.Parameters[1].Value!, sql.CommandText));
        });

        Assert.Equal(32, results.Count);
        Assert.Equal(Enumerable.Range(0, 32), results.Select(result => result.Age).OrderBy(value => value));
        Assert.All(results, result => Assert.Equal(5, result.Limit));
        Assert.Single(results.Select(result => result.Sql).Distinct());
    }

    [Fact]
    public void Hot_shape_cache_does_not_cross_different_fluent_models()
    {
        using var firstContext = new CacheMappingContextA();
        using var secondContext = new CacheMappingContextB();

        var first = BuildDescendingLimitFive(firstContext.Users, 20).ToQuerySql();
        var second = BuildDescendingLimitFive(secondContext.Users, 20).ToQuerySql();
        var firstAgain = BuildDescendingLimitFive(firstContext.Users, 42).ToQuerySql();

        Assert.Contains("\"cache_users_a\"", first.CommandText);
        Assert.Contains("\"cache_age_a\"", first.CommandText);
        Assert.Contains("\"cache_users_b\"", second.CommandText);
        Assert.Contains("\"cache_age_b\"", second.CommandText);
        Assert.DoesNotContain("cache_users_b", first.CommandText);
        Assert.DoesNotContain("cache_age_b", first.CommandText);
        Assert.DoesNotContain("cache_users_a", second.CommandText);
        Assert.DoesNotContain("cache_age_a", second.CommandText);
        Assert.Equal([20, 5], first.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([20, 5], second.Parameters.Select(parameter => parameter.Value).ToArray());
        Assert.Equal([42, 5], firstAgain.Parameters.Select(parameter => parameter.Value).ToArray());
    }

    private static IQueryable<RichUser> BuildDescendingLimitFive(TestDbContext db, int minimumAge) =>
        BuildDescendingLimitFive(db.RichUsers, minimumAge);

    private static IQueryable<RichUser> BuildDescendingLimitFive(IQueryable<RichUser> users, int minimumAge) =>
        users
            .Where(user => user.IsActive && user.Age >= minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(5);

    private static IQueryable<RichUser> BuildDescendingLimitTen(TestDbContext db, int minimumAge) =>
        db.RichUsers
            .Where(user => user.IsActive && user.Age >= minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(10);

    private static IQueryable<RichUser> BuildGreaterThan(TestDbContext db, int minimumAge) =>
        db.RichUsers
            .Where(user => user.IsActive && user.Age > minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(5);

    private static IQueryable<RichUser> BuildEqual(TestDbContext db, int minimumAge) =>
        db.RichUsers
            .Where(user => user.IsActive && user.Age == minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(5);

    private static IQueryable<RichUser> BuildAscendingLimitFive(TestDbContext db, int minimumAge) =>
        db.RichUsers
            .Where(user => user.IsActive && user.Age >= minimumAge)
            .OrderBy(user => user.CreatedAt)
            .Take(5);
}

public sealed class CacheMappingContextA : DbContext
{
    public CacheMappingContextA()
        : base(_ => { })
    {
    }

    public DbSet<RichUser> Users => Set<RichUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RichUser>(entity =>
        {
            entity.ToTable("cache_users_a");
            entity.Property(user => user.Age).HasColumnName("cache_age_a");
        });
    }
}

public sealed class CacheMappingContextB : DbContext
{
    public CacheMappingContextB()
        : base(_ => { })
    {
    }

    public DbSet<RichUser> Users => Set<RichUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RichUser>(entity =>
        {
            entity.ToTable("cache_users_b");
            entity.Property(user => user.Age).HasColumnName("cache_age_b");
        });
    }
}
