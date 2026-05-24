using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class StringMethodSqlTests
{
    [Fact]
    public void Contains_translates_to_like_with_wrapped_parameter()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.Contains("lic")).ToQuerySql();

        Assert.Contains("e.\"user_name\" LIKE $1", sql.CommandText);
        Assert.Equal("%lic%", sql.Parameters[0].Value);
    }

    [Fact]
    public void StartsWith_translates_to_like_suffix_wildcard()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.StartsWith("Al")).ToQuerySql();

        Assert.Contains("e.\"user_name\" LIKE $1", sql.CommandText);
        Assert.Equal("Al%", sql.Parameters[0].Value);
    }

    [Fact]
    public void EndsWith_translates_to_like_prefix_wildcard()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.EndsWith("ce")).ToQuerySql();

        Assert.Contains("e.\"user_name\" LIKE $1", sql.CommandText);
        Assert.Equal("%ce", sql.Parameters[0].Value);
    }

    [Fact]
    public void ToLower_translates_to_lower_function()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.ToLower() == "alice").ToQuerySql();

        Assert.Contains("lower(e.\"user_name\") = $1", sql.CommandText);
    }

    [Fact]
    public void ToUpper_translates_to_upper_function()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.ToUpper() == "ALICE").ToQuerySql();

        Assert.Contains("upper(e.\"user_name\") = $1", sql.CommandText);
    }

    [Fact]
    public void Empty_string_is_parameterized_not_null()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName == "").ToQuerySql();

        Assert.Contains("e.\"user_name\" = $1", sql.CommandText);
        Assert.Equal("", sql.Parameters[0].Value);
    }

    [Fact]
    public void IsNullOrEmpty_translates_to_null_or_empty_string()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => string.IsNullOrEmpty(u.Status)).ToQuerySql();

        Assert.Contains("(e.\"status\" IS NULL OR e.\"status\" = '')", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void IsNullOrWhiteSpace_translates_to_null_or_trimmed_empty_string()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => string.IsNullOrWhiteSpace(u.Status)).ToQuerySql();

        Assert.Contains("(e.\"status\" IS NULL OR btrim(e.\"status\") = '')", sql.CommandText);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void String_length_translates_to_length_function()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.Length > 3).ToQuerySql();

        Assert.Contains("length(e.\"user_name\") > $1", sql.CommandText);
        Assert.Equal(3, sql.Parameters[0].Value);
    }

    [Fact]
    public void Trim_methods_translate_to_postgresql_trim_functions()
    {
        using var db = new TestDbContext();

        var trim = db.RichUsers.Where(u => u.UserName.Trim() == "alice").ToQuerySql();
        var trimStart = db.RichUsers.Where(u => u.UserName.TrimStart().StartsWith("a")).ToQuerySql();
        var trimEnd = db.RichUsers.Where(u => u.UserName.TrimEnd().EndsWith("e")).ToQuerySql();

        Assert.Contains("btrim(e.\"user_name\") = $1", trim.CommandText);
        Assert.Contains("ltrim(e.\"user_name\") LIKE $1", trimStart.CommandText);
        Assert.Contains("rtrim(e.\"user_name\") LIKE $1", trimEnd.CommandText);
    }

    [Fact]
    public void Substring_translates_to_postgresql_one_based_substring()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.Substring(1, 3) == "lic").ToQuerySql();

        Assert.Contains("substring(e.\"user_name\" from $1 for $2) = $3", sql.CommandText);
        Assert.Equal([2, 3, "lic"], sql.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Substring_without_length_translates_to_open_ended_substring()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers.Where(u => u.UserName.Substring(6) == "Alice").ToQuerySql();

        Assert.Contains("substring(e.\"user_name\" from $1) = $2", sql.CommandText);
        Assert.Equal([7, "Alice"], sql.Parameters.Select(p => p.Value).ToArray());
    }
}
