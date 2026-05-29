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

    public DbContextOptionsBuilder UseCommandTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout cannot be negative.");
        }

        _options.CommandTimeout = timeout;
        return this;
    }

    public DbContextOptionsBuilder UseConnectionTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Connection timeout cannot be negative.");
        }

        _options.ConnectionTimeout = timeout;
        return this;
    }

    public DbContextOptionsBuilder EnableConnectionPooling(bool enabled = true)
    {
        _options.PoolingEnabled = enabled;
        return this;
    }

    public DbContextOptionsBuilder UseMinPoolSize(int minPoolSize)
    {
        if (minPoolSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minPoolSize), "Minimum pool size cannot be negative.");
        }

        _options.MinPoolSize = minPoolSize;
        return this;
    }

    public DbContextOptionsBuilder UseMaxPoolSize(int maxPoolSize)
    {
        if (maxPoolSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "Maximum pool size must be greater than zero.");
        }

        _options.MaxPoolSize = maxPoolSize;
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
            SensitiveLoggingEnabled = _options.SensitiveLoggingEnabled,
            CommandTimeout = _options.CommandTimeout,
            ConnectionTimeout = _options.ConnectionTimeout,
            PoolingEnabled = _options.PoolingEnabled,
            MinPoolSize = _options.MinPoolSize,
            MaxPoolSize = _options.MaxPoolSize
        };
    }
}
