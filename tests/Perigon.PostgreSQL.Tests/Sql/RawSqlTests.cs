using Perigon.PostgreSQL.RawSql;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class RawSqlTests
{
    [Fact]
    public void Raw_sql_query_uses_positional_parameters()
    {
        using var db = new TestDbContext();
        var name = "Alice";
        var age = 18;

        var sql = db.SqlQuery<RichUser>($"select * from users where name = {name} and age > {age}").ToBoundSql();

        Assert.Equal("select * from users where name = $1 and age > $2", sql.CommandText);
        Assert.Equal([name, age], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Raw_sql_command_uses_positional_parameters()
    {
        using var db = new TestDbContext();
        var id = 10;

        var sql = db.SqlCommand($"delete from users where id = {id}").ToBoundSql();

        Assert.Equal("delete from users where id = $1", sql.CommandText);
        Assert.Equal(id, sql.Parameters[0].Value);
    }

    [Fact]
    public void Raw_sql_injection_payload_stays_parameter_value()
    {
        using var db = new TestDbContext();
        var payload = "' OR 1=1 --";

        var sql = db.SqlQuery<RichUser>($"select * from users where name = {payload}").ToBoundSql();

        Assert.Equal("select * from users where name = $1", sql.CommandText);
        Assert.Equal(payload, sql.Parameters[0].Value);
    }

    [Fact]
    public void Raw_sql_command_without_parameters_keeps_text()
    {
        using var db = new TestDbContext();

        var sql = db.SqlCommand($"vacuum").ToBoundSql();

        Assert.Equal("vacuum", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Raw_sql_query_preserves_argument_order_with_repeated_values()
    {
        using var db = new TestDbContext();
        var status = "active";
        var minAge = 18;

        var sql = db.SqlQuery<RichUser>($"select * from users where status = {status} or backup_status = {status} and age >= {minAge}").ToBoundSql();

        Assert.Equal("select * from users where status = $1 or backup_status = $2 and age >= $3", sql.CommandText);
        Assert.Equal([status, status, minAge], sql.Parameters.Select(p => p.Value).ToArray());
    }
}
