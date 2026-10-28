using Npgsql;
using Microsoft.Extensions.Logging;

namespace Perigon.PostgreSQL.Options;

public class DbContextOptions
{
    public string? ConnectionString { get; internal set; }

    public NpgsqlDataSource? DataSource { get; internal set; }

    public bool SensitiveLoggingEnabled { get; internal set; }

    public ILoggerFactory? LoggerFactory { get; internal set; }

    public TimeSpan? CommandTimeout { get; internal set; }

    public TimeSpan? ConnectionTimeout { get; internal set; }

    public bool? PoolingEnabled { get; internal set; }

    public int? MinPoolSize { get; internal set; }

    public int? MaxPoolSize { get; internal set; }

    public string MigrationHistoryTableName { get; internal set; } = "_PerigonMigrationsHistory";

    public string MigrationHistorySchema { get; internal set; } = "public";

    internal string BuildEffectiveConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string was not configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (ConnectionTimeout is not null)
        {
            builder.Timeout = TimeoutSeconds(ConnectionTimeout.Value, nameof(ConnectionTimeout));
        }

        if (CommandTimeout is not null)
        {
            builder.CommandTimeout = TimeoutSeconds(CommandTimeout.Value, nameof(CommandTimeout));
        }

        if (PoolingEnabled is not null)
        {
            builder.Pooling = PoolingEnabled.Value;
        }

        if (MinPoolSize is not null)
        {
            builder.MinPoolSize = MinPoolSize.Value;
        }

        if (MaxPoolSize is not null)
        {
            builder.MaxPoolSize = MaxPoolSize.Value;
        }

        return builder.ConnectionString;
    }

    internal NpgsqlDataSource CreateDataSource()
    {
        return NpgsqlDataSource.Create(BuildEffectiveConnectionString());
    }

    private static int TimeoutSeconds(TimeSpan timeout, string parameterName)
    {
        if (timeout == TimeSpan.Zero)
        {
            return 0;
        }

        var seconds = Math.Ceiling(timeout.TotalSeconds);
        if (seconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout is too large.");
        }

        return (int)seconds;
    }
}

public sealed class DbContextOptions<TContext> : DbContextOptions
    where TContext : DbContext
{
}
