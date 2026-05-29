using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class AdvancedWhereSqlTests
{
    [Fact]
    public void Captured_null_variable_uses_is_null()
    {
        using var db = new TestDbContext();
        string? status = null;

        var sql = db.RichUsers.Where(u => u.Status == status).ToQuerySql();

        Assert.Contains("e.\"status\" IS NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Captured_non_null_variable_is_parameterized()
    {
        using var db = new TestDbContext();
        var status = "active";

        var sql = db.RichUsers.Where(u => u.Status == status).ToQuerySql();

        Assert.Contains("e.\"status\" = $1", sql.CommandText);
        Assert.Equal("active", sql.Parameters[0].Value);
    }

    [Fact]
    public void Null_inequality_uses_is_not_null()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.Status != null).ToQuerySql();

        Assert.Contains("e.\"status\" IS NOT NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Left_null_constant_comparison_uses_is_null()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => null == u.Status).ToQuerySql();

        Assert.Contains("e.\"status\" IS NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Left_null_constant_inequality_uses_is_not_null()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => null != u.Status).ToQuerySql();

        Assert.Contains("e.\"status\" IS NOT NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Nullable_has_value_translates_to_is_not_null()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UpdatedAt.HasValue).ToQuerySql();

        Assert.Contains("e.\"updated_at\" IS NOT NULL", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Negated_nullable_has_value_preserves_null_check_semantics()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => !u.UpdatedAt.HasValue).ToQuerySql();

        Assert.Contains("NOT (e.\"updated_at\" IS NOT NULL)", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Nullable_value_comparison_translates_to_column_comparison()
    {
        using var db = new TestDbContext();
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = db.RichUsers.Where(u => u.UpdatedAt!.Value > cutoff).ToQuerySql();

        Assert.Contains("e.\"updated_at\" > $1", sql.CommandText);
        Assert.Equal(cutoff, sql.Parameters[0].Value);
    }

    [Fact]
    public void Boolean_property_can_be_used_as_predicate()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.IsActive).ToQuerySql();

        Assert.Contains("WHERE e.\"is_active\"", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Negated_boolean_property_uses_not()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => !u.IsActive).ToQuerySql();

        Assert.Contains("WHERE NOT (e.\"is_active\")", sql.CommandText);
    }

    [Fact]
    public void Date_range_uses_two_parameters_in_order()
    {
        using var db = new TestDbContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = db.RichUsers.Where(u => u.CreatedAt >= start && u.CreatedAt < end).ToQuerySql();

        Assert.Contains("e.\"created_at\" >= $1", sql.CommandText);
        Assert.Contains("e.\"created_at\" < $2", sql.CommandText);
        Assert.Equal([start, end], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Multiple_where_calls_preserve_parameter_order()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Age > 18)
            .Where(u => u.Status == "active")
            .ToQuerySql();

        Assert.Contains("WHERE (e.\"age\" > $1) AND (e.\"status\" = $2)", sql.CommandText);
        Assert.Equal([18, "active"], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Or_expression_is_parenthesized()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Status == "active" || u.Status == "pending")
            .ToQuerySql();

        Assert.Contains("((e.\"status\" = $1) OR (e.\"status\" = $2))", sql.CommandText);
    }
}
