using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class JsonbSqlTests
{
    [Fact]
    public void JsonbContains_translates_to_containment_operator()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.ProfileJson.JsonbContains("""{"level":3}""")).ToQuerySql();

        Assert.Contains("e.\"profile_json\" @> $1", sql.CommandText);
        Assert.Equal("""{"level":3}""", sql.Parameters[0].Value);
    }

    [Fact]
    public void JsonbHasKey_translates_to_key_exists_operator()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.ProfileJson.JsonbHasKey("name")).ToQuerySql();

        Assert.Contains("e.\"profile_json\" ? $1", sql.CommandText);
        Assert.Equal("name", sql.Parameters[0].Value);
    }

    [Fact]
    public void JsonbText_translates_to_text_extract_operator()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.ProfileJson.JsonbText("name") == "Alice").ToQuerySql();

        Assert.Contains("e.\"profile_json\" ->> $1", sql.CommandText);
        Assert.Contains("= $2", sql.CommandText);
        Assert.Equal(["name", "Alice"], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void JsonbPathExists_translates_to_jsonpath_exists_operator()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.ProfileJson.JsonbPathExists("$.level ? (@ > 2)")).ToQuerySql();

        Assert.Contains("e.\"profile_json\" @? ($1)::jsonpath", sql.CommandText);
        Assert.Equal("$.level ? (@ > 2)", sql.Parameters[0].Value);
    }
}
