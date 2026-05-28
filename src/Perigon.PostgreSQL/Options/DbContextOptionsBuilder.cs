using Npgsql;

namespace Perigon.PostgreSQL.Options;

public sealed class DbContextOptionsBuilder
{
    private readonly DbContextOptions _options = new();

    public DbContextOptionsBuilder UsePostgres(string connectionString)
    {
        _options.ConnectionString = connectionString;
        _options.DataSource = null;
        return this;
    }

    public DbContextOptionsBuilder UseNpgsql(string connectionString)
    {
        return UsePostgres(connectionString);
    }

    public DbContextOptionsBuilder UsePostgres(NpgsqlDataSource dataSource)
    {
        _options.DataSource = dataSource;
        _options.ConnectionString = null;
        return this;
    }

    public DbContextOptionsBuilder UseNpgsql(NpgsqlDataSource dataSource)
    {
        return UsePostgres(dataSource);
    }

    public DbContextOptionsBuilder EnableSensitiveLogging(bool enabled = true)
    {
        _options.SensitiveLoggingEnabled = enabled;
        return this;
    }

    internal DbContextOptions Build()
    {
        return _options;
    }

    internal DbContextOptions<TContext> Build<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptions<TContext>
        {
            ConnectionString = _options.ConnectionString,
            DataSource = _options.DataSource,
            SensitiveLoggingEnabled = _options.SensitiveLoggingEnabled
        };
    }
}
