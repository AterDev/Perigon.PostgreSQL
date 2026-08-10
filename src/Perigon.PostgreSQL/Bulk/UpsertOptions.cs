namespace Perigon.PostgreSQL.Bulk;

public sealed class UpsertOptions<T> where T : class
{
    public bool DoNothing { get; init; }

    /// <summary>
    /// Maximum number of entities emitted in one INSERT ... ON CONFLICT command.
    /// Multiple commands created by one UpsertManyAsync call are committed atomically.
    /// </summary>
    public int BatchSize { get; init; } = 1000;
}
