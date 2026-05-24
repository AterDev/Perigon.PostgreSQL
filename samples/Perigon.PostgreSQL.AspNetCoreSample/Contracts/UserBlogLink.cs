namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserBlogLink
{
    public int UserId { get; set; }

    public string UserName { get; set; } = "";

    public int BlogId { get; set; }

    public string BlogName { get; set; } = "";
}
