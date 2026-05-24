using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Tests.Metadata;

public sealed class NamingConventionTests
{
    [Theory]
    [InlineData("UserName", "user_name")]
    [InlineData("URLValue", "url_value")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("CreatedAtUtc", "created_at_utc")]
    public void ToSnakeCase_handles_common_dotnet_names(string input, string expected)
    {
        Assert.Equal(expected, NamingConventions.ToSnakeCase(input));
    }

    [Fact]
    public void Quote_escapes_embedded_quotes()
    {
        Assert.Equal("\"a\"\"b\"", Identifier.Quote("a\"b"));
    }

    [Fact]
    public void Qualify_quotes_schema_and_table()
    {
        Assert.Equal("\"audit\".\"users\"", Identifier.Qualify("audit", "users"));
    }
}
