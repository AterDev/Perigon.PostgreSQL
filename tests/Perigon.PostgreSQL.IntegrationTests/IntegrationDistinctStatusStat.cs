namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationDistinctStatusStat
{
    public string? Status { get; set; }

    public long DistinctActiveStates { get; set; }
}
