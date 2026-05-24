namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserStatusStat
{
    public string? Status { get; set; }

    public bool IsActive { get; set; }

    public long Count { get; set; }

    public double AverageAge { get; set; }
}
