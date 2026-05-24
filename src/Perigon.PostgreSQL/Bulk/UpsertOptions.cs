namespace Perigon.PostgreSQL.Bulk;

public sealed class UpsertOptions<T> where T : class
{
    public bool DoNothing { get; init; }
}
