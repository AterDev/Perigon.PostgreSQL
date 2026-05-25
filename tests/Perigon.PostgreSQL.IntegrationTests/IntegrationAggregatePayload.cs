namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationAggregatePayload
{
    public string? Status { get; set; }

    public string[] Names { get; set; } = [];

    public string Profiles { get; set; } = string.Empty;
}