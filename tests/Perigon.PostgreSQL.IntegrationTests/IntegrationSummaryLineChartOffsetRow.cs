namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationSummaryLineChartOffsetRow
{
    public DateTimeOffset Date { get; set; }

    public long Count { get; set; }

    public decimal TotalAmount { get; set; }
}