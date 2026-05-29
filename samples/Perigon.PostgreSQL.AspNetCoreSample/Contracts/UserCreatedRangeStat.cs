namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserCreatedRangeStat
{
    public DateTimeOffset? FromUtc { get; set; }

    public DateTimeOffset? ToUtc { get; set; }

    public long Count { get; set; }

    public double? AverageAge { get; set; }

    public string[] UserNames { get; set; } = [];
}