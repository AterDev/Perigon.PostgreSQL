using System.Linq.Expressions;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Tests.Models;

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

    [Fact]
    public void Reusing_query_after_captured_predicate_value_change_rebinds_parameters()
    {
        using var db = new TestDbContext();
        var minimumAge = 20;
        var query = db.RichUsers.Where(u => u.Age >= minimumAge);

        _ = query.ToQuerySql();
        minimumAge = 42;

        var sql = query.ToQuerySql();

        Assert.Equal([minimumAge], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Reusing_query_after_captured_null_changes_rebuilds_null_semantics()
    {
        using var db = new TestDbContext();
        string? status = null;
        var query = db.RichUsers.Where(u => u.Status == status);

        var nullSql = query.ToQuerySql();
        status = "active";
        var valueSql = query.ToQuerySql();

        Assert.Contains("IS NULL", nullSql.CommandText);
        Assert.DoesNotContain("IS NULL", valueSql.CommandText);
        Assert.Equal(["active"], valueSql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Manually_built_take_member_expression_reads_current_value()
    {
        using var db = new TestDbContext();
        var holder = new PagingHolder { Value = 3 };
        var takeArgument = Expression.Field(Expression.Constant(holder), nameof(PagingHolder.Value));
        var expression = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Take),
            [typeof(RichUser)],
            db.RichUsers.Expression,
            takeArgument);
        var query = db.RichUsers.Provider.CreateQuery<RichUser>(expression);

        _ = query.ToQuerySql();
        holder.Value = 7;

        var sql = query.ToQuerySql();

        Assert.Equal([7], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Reverse_comparison_keeps_entity_on_left_semantics()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => 5 < u.Age).ToQuerySql();

        Assert.Contains("$1 < e.\"age\"", sql.CommandText);
        Assert.Equal(5, sql.Parameters[0].Value);
    }

    [Fact]
    public void AndAlso_simple_predicates_use_cached_template_shape()
    {
        using var db = new TestDbContext();
        var minimumAge = 20;
        var query = db.RichUsers.Where(u => u.IsActive && u.Age >= minimumAge);

        _ = query.ToQuerySql();
        minimumAge = 40;
        var sql = query.ToQuerySql();

        Assert.Contains("e.\"is_active\"", sql.CommandText);
        Assert.Contains("e.\"age\" >= $1", sql.CommandText);
        Assert.Equal([minimumAge], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Concurrent_first_hits_on_same_expression_are_race_free()
    {
        using var db = new TestDbContext();
        var minimumAge = 30;
        var query = db.RichUsers.Where(u => u.Age >= minimumAge);
        var outputs = new System.Collections.Concurrent.ConcurrentBag<BoundSql>();

        Parallel.For(0, 32, _ => outputs.Add(query.ToQuerySql()));

        Assert.Equal(32, outputs.Count);
        Assert.All(outputs, sql =>
        {
            Assert.Contains("e.\"age\" >= $1", sql.CommandText);
            Assert.Equal(minimumAge, sql.Parameters[0].Value);
        });
    }

    [Fact]
    public void Cached_expression_handles_null_value_transitions_without_stale_sql()
    {
        using var db = new TestDbContext();
        string? status = null;
        var query = db.RichUsers.Where(u => u.Status == status);

        var nullSql = query.ToQuerySql();
        status = "active";
        var valueSql = query.ToQuerySql();
        status = null;
        var nullAgainSql = query.ToQuerySql();

        Assert.Contains("IS NULL", nullSql.CommandText);
        Assert.Contains("= $1", valueSql.CommandText);
        Assert.Contains("IS NULL", nullAgainSql.CommandText);
        Assert.Empty(nullSql.Parameters);
        Assert.Equal(["active"], valueSql.Parameters.Select(p => p.Value).ToArray());
        Assert.Empty(nullAgainSql.Parameters);
    }

    [Fact]
    public void Same_shape_across_context_models_keeps_mapping_isolated()
    {
        using var firstContext = new TestDbContext();
        using var secondContext = new TestDbContext();
        var minimum = 20;

        var first = firstContext.RichUsers.Where(u => u.Age >= minimum).ToQuerySql();
        var second = secondContext.AttributedUsers.Where(u => u.Key >= minimum).ToQuerySql();

        Assert.Contains("\"rich_users\"", first.CommandText);
        Assert.Contains("\"security\".\"app_users\"", second.CommandText);
        Assert.Contains("\"user_id\"", second.CommandText);
        Assert.NotEqual(first.CommandText, second.CommandText);
    }

    [Fact]
    public void Complex_property_getter_uses_the_existing_fallback_evaluation_behavior()
    {
        using var db = new TestDbContext();
        var holder = new GetterHolder(20);

        var query = db.RichUsers.Where(u => u.Age >= holder.Value);
        _ = query.ToQuerySql();
        _ = query.ToQuerySql();
        _ = query.ToQuerySql();

        // The unsupported property getter is rejected by the template parser;
        // each execution then follows the existing fallback path once.
        Assert.Equal(3, holder.ReadCount);
    }

    private sealed class PagingHolder
    {
        public int Value;
    }

    private sealed class GetterHolder(int value)
    {
        private readonly int _value = value;

        public int ReadCount { get; private set; }

        public int Value
        {
            get
            {
                ReadCount++;
                return _value;
            }
        }
    }

}
