namespace Perigon.PostgreSQL.Bulk;

public sealed class ReturningOptions<T> where T : class
{
    public bool ReturnAllColumns { get; init; } = true;

    public int BatchSize { get; init; } = 1000;
}
