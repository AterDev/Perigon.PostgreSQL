using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class InsertManySqlTests
{
    [Fact]
    public void Insert_many_returning_generates_multi_values_insert()
    {
        using var db = new TestDbContext();
        var rows = new[]
        {
            new RichUser { UserName = "Alice", Age = 30, IsActive = true },
            new RichUser { UserName = "Bob", Age = 40, IsActive = false }
        };

        var sql = db.RichUsers.ToInsertManyReturningSql(rows);

        Assert.Equal(
            "INSERT INTO \"rich_users\" (\"user_name\", \"age\", \"is_active\", \"status\", \"created_at\", \"updated_at\", \"tags\", \"profile_json\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8), ($9, $10, $11, $12, $13, $14, $15, $16) RETURNING \"id\", \"user_name\", \"age\", \"is_active\", \"status\", \"created_at\", \"updated_at\", \"tags\", \"profile_json\"",
            sql.CommandText);
    }

    [Fact]
    public void Insert_many_returning_empty_rows_is_rejected_for_sql_preview()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<InvalidOperationException>(() =>
            db.RichUsers.ToInsertManyReturningSql([]));

        Assert.Contains("at least one entity", error.Message);
    }

    [Fact]
    public void Bulk_insert_values_generates_multi_values_insert_without_returning()
    {
        using var db = new TestDbContext();
        var rows = new[]
        {
            new RichUser { UserName = "Alice", Age = 30, IsActive = true },
            new RichUser { UserName = "Bob", Age = 40, IsActive = false }
        };

        var sql = db.RichUsers.ToBulkInsertValuesSql(rows);

        Assert.Equal(
            "INSERT INTO \"rich_users\" (\"user_name\", \"age\", \"is_active\", \"status\", \"created_at\", \"updated_at\", \"tags\", \"profile_json\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8), ($9, $10, $11, $12, $13, $14, $15, $16)",
            sql.CommandText);
    }

    [Fact]
    public void Bulk_insert_values_empty_rows_is_rejected_for_sql_preview()
    {
        using var db = new TestDbContext();

        var error = Assert.Throws<InvalidOperationException>(() =>
            db.RichUsers.ToBulkInsertValuesSql([]));

        Assert.Contains("at least one entity", error.Message);
    }
}
