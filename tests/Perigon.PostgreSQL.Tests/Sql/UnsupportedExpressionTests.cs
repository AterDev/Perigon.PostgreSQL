using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Expressions;
using System.Globalization;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class UnsupportedExpressionTests
{
    [Fact]
    public void Local_method_call_is_rejected()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => IsAdult(u.Age)).ToQuerySql());

        Assert.Contains("IsAdult", error.Message);
    }

    [Fact]
    public void Unmapped_property_is_rejected()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<InvalidOperationException>(() =>
            db.AttributedUsers.Where(u => u.RuntimeOnly == "x").ToQuerySql());

        Assert.Contains("RuntimeOnly", error.Message);
    }

    [Fact]
    public void Unsupported_static_method_call_is_rejected()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => Math.Abs(u.Age) > 10).ToQuerySql());

        Assert.Contains("Abs", error.Message);
    }

    [Fact]
    public void Unknown_queryable_operator_is_rejected()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Reverse().ToQuerySql());

        Assert.Contains("Reverse", error.Message);
    }

    [Fact]
    public void Unsupported_queryable_composition_operators_are_rejected()
    {
        using var db = new TestDbContext();

        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Concat(db.RichUsers).ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.TakeWhile(user => user.Age > 18).ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.SkipWhile(user => user.Age < 18).ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.SelectMany(_ => db.RichUsers).ToQuerySql());
    }

    [Fact]
    public void String_comparison_overloads_are_rejected()
    {
        using var db = new TestDbContext();

        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => u.UserName.Contains("a", StringComparison.Ordinal)).ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => u.UserName.StartsWith("a", StringComparison.Ordinal)).ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => u.UserName.EndsWith("a", StringComparison.Ordinal)).ToQuerySql());
    }

    [Fact]
    public void String_culture_overloads_are_rejected()
    {
        using var db = new TestDbContext();

        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => u.UserName.ToLower(CultureInfo.InvariantCulture) == "alice").ToQuerySql());
        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers.Where(u => u.UserName.ToUpper(CultureInfo.InvariantCulture) == "ALICE").ToQuerySql());
    }

    [Fact]
    public void Projected_string_comparison_overload_is_rejected()
    {
        using var db = new TestDbContext();

        Assert.Throws<UnsupportedQueryExpressionException>(() =>
            db.RichUsers
                .GroupBy(u => u.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .Where(x => x.Status!.Contains("a", StringComparison.Ordinal))
                .ToSql());
    }

    private static bool IsAdult(int age)
    {
        return age >= 18;
    }
}
