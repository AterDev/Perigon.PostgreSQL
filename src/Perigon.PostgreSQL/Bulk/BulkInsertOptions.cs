namespace Perigon.PostgreSQL.Bulk;

public sealed class BulkInsertOptions
{
    public int BatchSize { get; init; } = 1000;

    public BulkInsertMode Mode { get; init; } = BulkInsertMode.Copy;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
