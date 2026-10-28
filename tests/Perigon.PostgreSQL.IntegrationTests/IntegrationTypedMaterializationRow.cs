namespace Perigon.PostgreSQL.IntegrationTests;

public enum IntegrationStatusCode
{
    Unknown,
    Active,
    Inactive
}

public sealed class IntegrationTypedMaterializationRow
{
    public long UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public IntegrationStatusCode StatusCode { get; set; }
}
