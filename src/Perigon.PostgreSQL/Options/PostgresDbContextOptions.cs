using Npgsql;

namespace Perigon.PostgreSQL.Options;

public sealed class PostgresDbContextOptions
{
    public string? ConnectionString { get; internal set; }

    public NpgsqlDataSource? DataSource { get; internal set; }

    public bool SensitiveLoggingEnabled { get; internal set; }
}
