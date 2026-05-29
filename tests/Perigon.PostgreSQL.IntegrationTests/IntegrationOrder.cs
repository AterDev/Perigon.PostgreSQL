namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationOrder
{
    public int Id { get; set; }

    public string OrderNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTime OrderTime { get; set; }

    public decimal TotalPrice { get; set; }
}