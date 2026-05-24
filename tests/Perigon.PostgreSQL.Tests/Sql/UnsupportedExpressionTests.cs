using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Expressions;

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

    private static bool IsAdult(int age)
    {
        return age >= 18;
    }
}
