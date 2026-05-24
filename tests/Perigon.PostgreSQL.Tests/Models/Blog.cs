namespace Perigon.PostgreSQL.Tests.Models;

public sealed class Blog
{
    public int Id { get; set; }

    public int RichUserId { get; set; }

    public string Name { get; set; } = "";

    public bool IsPublic { get; set; }
}
