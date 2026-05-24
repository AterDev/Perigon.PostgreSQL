namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationUserStatusStat
{
    public string? Status { get; set; }

    public bool IsActive { get; set; }

    public long Count { get; set; }

    public double AverageAge { get; set; }
}
