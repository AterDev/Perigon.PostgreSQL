namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserDistinctActiveStat
{
    public string? Status { get; set; }

    public long DistinctActiveStates { get; set; }
}
