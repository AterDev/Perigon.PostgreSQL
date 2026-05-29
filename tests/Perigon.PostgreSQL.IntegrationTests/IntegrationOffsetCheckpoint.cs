namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationOffsetCheckpoint
{
    public int Id { get; set; }

    public string CheckpointNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}