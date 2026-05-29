namespace Perigon.PostgreSQL.Tests.Models;

public sealed class SummaryLineChartOffsetRow
{
    public DateTimeOffset Date { get; set; }

    public int Count { get; set; }

    public decimal TotalAmount { get; set; }
}