using Npgsql;

namespace Perigon.PostgreSQL.Options;

public sealed class PostgresDbContextOptionsBuilder
{
    private readonly PostgresDbContextOptions _options = new();

    public PostgresDbContextOptionsBuilder UsePostgres(string connectionString)
    {
        _options.ConnectionString = connectionString;
        _options.DataSource = null;
        return this;
    }

    public PostgresDbContextOptionsBuilder UsePostgres(NpgsqlDataSource dataSource)
    {
        _options.DataSource = dataSource;
        _options.ConnectionString = null;
        return this;
    }

    public PostgresDbContextOptionsBuilder EnableSensitiveLogging(bool enabled = true)
    {
        _options.SensitiveLoggingEnabled = enabled;
        return this;
    }

    internal PostgresDbContextOptions Build()
    {
        return _options;
    }
}
