namespace Perigon.PostgreSQL.Tests.Models;

public sealed class StatisticOrder
{
    public int Id { get; set; }

    public string OrderNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTime OrderTime { get; set; }

    public decimal TotalPrice { get; set; }
}