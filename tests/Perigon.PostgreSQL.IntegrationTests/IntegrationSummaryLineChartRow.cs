namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationSummaryLineChartRow
{
    public DateTime Date { get; set; }

    public long Count { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Status { get; set; }
}