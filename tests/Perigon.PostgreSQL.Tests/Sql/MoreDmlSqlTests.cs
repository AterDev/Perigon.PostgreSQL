using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Update;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class MoreDmlSqlTests
{
    [Fact]
    public void Full_table_delete_can_be_explicitly_allowed()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.ToDeleteSql(new DeleteOptions { AllowFullTableDelete = true });

        Assert.Equal("DELETE FROM \"rich_users\" AS e", sql.CommandText);
    }

    [Fact]
    public void Multiple_update_setters_preserve_call_order()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Id == 7)
            .ToUpdateSql(s => s
                .Set(u => u.Status, "active")
                .Set(u => u.Age, 42));

        Assert.Equal("UPDATE \"rich_users\" AS e SET \"status\" = $1, \"age\" = $2 WHERE (e.\"id\" = $3)", sql.CommandText);
        Assert.Equal(["active", 42, 7], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Insert_sql_respects_schema_and_not_mapped_columns()
    {
        using var db = new TestDbContext();
        var user = new AttributedUser
        {
            Name = "Alice",
            Roles = ["admin"],
            RuntimeOnly = "not persisted"
        };

        var sql = db.AttributedUsers.ToInsertSql(user);

        Assert.Equal(
            "INSERT INTO \"security\".\"app_users\" (\"display_name\", \"roles\") VALUES ($1, $2) RETURNING \"user_id\", \"display_name\", \"roles\"",
            sql.CommandText);
    }

    [Fact]
    public void Update_to_null_is_parameterized()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Id == 1)
            .ToUpdateSql(s => s.Set<string?>(u => u.Status, null));

        Assert.Equal("UPDATE \"rich_users\" AS e SET \"status\" = $1 WHERE (e.\"id\" = $2)", sql.CommandText);
        Assert.Null(sql.Parameters[0].Value);
    }

    [Fact]
    public void Update_can_set_column_from_expression()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Id == 1)
            .ToUpdateSql(s => s.SetExpression(u => u.Age, u => u.Age + 1));

        Assert.Equal("UPDATE \"rich_users\" AS e SET \"age\" = (e.\"age\" + $1) WHERE (e.\"id\" = $2)", sql.CommandText);
        Assert.Equal([1, 1], sql.Parameters.Select(p => p.Value).ToArray());
    }
}
