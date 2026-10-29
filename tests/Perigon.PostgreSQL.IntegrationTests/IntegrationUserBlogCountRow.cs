namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationUserBlogCountRow
{
    public int UserId { get; set; }

    public long BlogCount { get; set; }

    public int IntBlogCount { get; set; }

    public int BlogIdSum { get; set; }

    public int? BlogIdMin { get; set; }

    public int? BlogIdMax { get; set; }

    public double? BlogIdAverage { get; set; }
}
