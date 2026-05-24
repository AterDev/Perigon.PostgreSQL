using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Update;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class DmlSqlTests
{
    [Fact]
    public void Insert_sql_uses_writable_columns_and_returning()
    {
        using var db = new TestDbContext();
        var user = new ConventionUser
        {
            UserName = "Alice",
            Age = 30,
            Status = "active",
            Tags = ["developer"]
        };

        var sql = db.ConventionUsers.ToInsertSql(user);

        Assert.Equal(
            "INSERT INTO \"convention_users\" (\"user_name\", \"age\", \"status\", \"tags\") VALUES ($1, $2, $3, $4) RETURNING \"id\", \"user_name\", \"age\", \"status\", \"tags\"",
            sql.CommandText);
        Assert.Equal(["Alice", 30, "active"], sql.Parameters.Take(3).Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Insert_sql_can_disable_returning()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers.ToInsertSql(new ConventionUser { UserName = "Bob" }, returning: false);

        Assert.DoesNotContain("RETURNING", sql.CommandText);
    }

    [Fact]
    public void Delete_sql_requires_where_by_default()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<InvalidOperationException>(() => db.ConventionUsers.ToDeleteSql());

        Assert.Contains("DELETE without WHERE", error.Message);
    }

    [Fact]
    public void Delete_sql_with_where_is_parameterized()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers
            .Where(u => u.Age < 18)
            .ToDeleteSql();

        Assert.Equal("DELETE FROM \"convention_users\" AS e WHERE (e.\"age\" < $1)", sql.CommandText);
        Assert.Equal(18, sql.Parameters[0].Value);
    }

    [Fact]
    public void Update_sql_requires_where_by_default()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<InvalidOperationException>(() =>
            db.ConventionUsers.ToUpdateSql(s => s.Set(u => u.Status, "retired")));

        Assert.Contains("UPDATE without WHERE", error.Message);
    }

    [Fact]
    public void Update_sql_uses_setter_dsl()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers
            .Where(u => u.Age > 60)
            .ToUpdateSql(s => s.Set(u => u.Status, "retired"));

        Assert.Equal(
            "UPDATE \"convention_users\" AS e SET \"status\" = $1 WHERE (e.\"age\" > $2)",
            sql.CommandText);
        Assert.Equal(["retired", 60], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Full_table_update_can_be_explicitly_allowed()
    {
        using var db = new TestDbContext();

        var sql = db.ConventionUsers.ToUpdateSql(
            s => s.Set(u => u.Status, "active"),
            new UpdateOptions { AllowFullTableUpdate = true });

        Assert.Equal("UPDATE \"convention_users\" AS e SET \"status\" = $1", sql.CommandText);
    }
}
