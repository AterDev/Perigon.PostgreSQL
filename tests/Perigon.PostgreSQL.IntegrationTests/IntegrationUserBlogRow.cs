namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationUserBlogRow
{
    public int UserId { get; init; }

    public string UserName { get; init; } = "";

    public int BlogId { get; init; }

    public string BlogName { get; init; } = "";
}
