using Npgsql;

namespace Perigon.PostgreSQL.Options;

public class DbContextOptions
{
    public string? ConnectionString { get; internal set; }

    public NpgsqlDataSource? DataSource { get; internal set; }

    public bool SensitiveLoggingEnabled { get; internal set; }
}

public sealed class DbContextOptions<TContext> : DbContextOptions
    where TContext : DbContext
{
}
