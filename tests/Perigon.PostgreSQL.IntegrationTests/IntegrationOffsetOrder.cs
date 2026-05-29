namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationOffsetOrder
{
    public int Id { get; set; }

    public string OrderNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTimeOffset OrderTime { get; set; }

    public decimal TotalPrice { get; set; }
}