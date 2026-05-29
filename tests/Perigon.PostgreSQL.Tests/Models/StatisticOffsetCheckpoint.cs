namespace Perigon.PostgreSQL.Tests.Models;

public sealed class StatisticOffsetCheckpoint
{
    public int Id { get; set; }

    public string CheckpointNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}