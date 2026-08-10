namespace Perigon.PostgreSQL.Bulk;

public sealed class BulkInsertOptions
{
    /// <summary>
    /// Maximum number of rows per SQL batch when <see cref="Mode"/> is <see cref="BulkInsertMode.InsertValues"/>.
    /// COPY mode streams one PostgreSQL COPY statement and requires the default value.
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    public BulkInsertMode Mode { get; init; } = BulkInsertMode.Copy;

    /// <summary>
    /// Maximum duration of the bulk operation. Zero and <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disable this timeout.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
