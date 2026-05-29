namespace Perigon.PostgreSQL.Tests.Models;

public sealed class SummaryLineChartRow
{
    public DateTime Date { get; set; }

    public int Count { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Status { get; set; }
}