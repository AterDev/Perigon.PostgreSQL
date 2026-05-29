namespace Perigon.PostgreSQL.Tests.Models;

public sealed class StatisticOffsetOrder
{
    public int Id { get; set; }

    public string OrderNo { get; set; } = "";

    public string? Status { get; set; }

    public DateTimeOffset OrderTime { get; set; }

    public decimal TotalPrice { get; set; }
}