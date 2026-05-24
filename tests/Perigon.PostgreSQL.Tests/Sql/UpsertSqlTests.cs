using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class UpsertSqlTests
{
    [Fact]
    public void Upsert_many_generates_on_conflict_do_update()
    {
        using var db = new TestDbContext();
        var rows = new[]
        {
            new RichUser { UserName = "Alice", Age = 30, IsActive = true },
            new RichUser { UserName = "Bob", Age = 40, IsActive = false }
        };

        var sql = db.RichUsers.ToUpsertSql(rows, u => u.UserName);

        Assert.Equal(
            "INSERT INTO \"rich_users\" (\"user_name\", \"age\", \"is_active\", \"status\", \"created_at\", \"updated_at\", \"tags\", \"profile_json\") VALUES ($1, $2, $3, $4, $5, $6, $7, $8), ($9, $10, $11, $12, $13, $14, $15, $16) ON CONFLICT (\"user_name\") DO UPDATE SET \"age\" = EXCLUDED.\"age\", \"is_active\" = EXCLUDED.\"is_active\", \"status\" = EXCLUDED.\"status\", \"created_at\" = EXCLUDED.\"created_at\", \"updated_at\" = EXCLUDED.\"updated_at\", \"tags\" = EXCLUDED.\"tags\", \"profile_json\" = EXCLUDED.\"profile_json\"",
            sql.CommandText);
        Assert.Equal(16, sql.Parameters.Count);
    }

    [Fact]
    public void Upsert_many_can_do_nothing()
    {
        using var db = new TestDbContext();
        var rows = new[] { new RichUser { UserName = "Alice", Age = 30 } };

        var sql = db.RichUsers.ToUpsertSql(
            rows,
            u => u.UserName,
            new UpsertOptions<RichUser> { DoNothing = true });

        Assert.EndsWith("ON CONFLICT (\"user_name\") DO NOTHING", sql.CommandText);
    }

    [Fact]
    public void Upsert_many_supports_composite_key()
    {
        using var db = new TestDbContext();
        var rows = new[] { new RichUser { UserName = "Alice", Age = 30, Status = "active" } };

        var sql = db.RichUsers.ToUpsertSql(rows, u => new { u.UserName, u.Status });

        Assert.Contains("ON CONFLICT (\"user_name\", \"status\")", sql.CommandText);
    }
}
