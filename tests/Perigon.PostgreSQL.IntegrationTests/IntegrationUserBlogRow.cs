namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationUserBlogRow
{
    public int UserId { get; set; }

    public string UserName { get; set; } = "";

    public int BlogId { get; set; }

    public string BlogName { get; set; } = "";
}
