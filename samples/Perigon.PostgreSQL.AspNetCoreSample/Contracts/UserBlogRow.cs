namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserBlogRow
{
    public int UserId { get; set; }

    public string UserName { get; set; } = "";

    public int BlogId { get; set; }

    public string BlogName { get; set; } = "";

    public long PostCount { get; set; }
}
