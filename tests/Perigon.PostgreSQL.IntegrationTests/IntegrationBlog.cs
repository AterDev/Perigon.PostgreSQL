namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationBlog
{
    public int Id { get; set; }

    public int IntegrationUserId { get; set; }

    public string Name { get; set; } = "";

    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; }
}
