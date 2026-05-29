using Npgsql;
using System.Diagnostics.CodeAnalysis;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.RawSql;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL;

public abstract class DbContext : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<Type, object> _sets = [];
    private readonly DbContextOptions _options;
    private NpgsqlDataSource? _ownedDataSource;
    private NpgsqlConnection? _transactionConnection;
    private NpgsqlTransaction? _transaction;

    protected DbContext()
    {
        var builder = new DbContextOptionsBuilder();
        OnConfiguring(builder);
        _options = builder.Build();
    }

    protected DbContext(Action<DbContextOptionsBuilder> configure)
    {
        var builder = new DbContextOptionsBuilder();
        configure(builder);
        OnConfiguring(builder);
        _options = builder.Build();
    }

    protected DbContext(DbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    protected virtual void OnConfiguring(DbContextOptionsBuilder builder)
    {
    }

    protected virtual void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    protected DbSet<T> Set<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class
    {
        if (_sets.TryGetValue(typeof(T), out var existing))
        {
            return (DbSet<T>)existing;
        }

        var model = ResolveEntityModel(typeof(T));
        var set = new DbSet<T>(this, model);
        _sets.Add(typeof(T), set);
        return set;
    }

    public NpgsqlDataSource GetDataSource()
    {
        if (_options.DataSource is not null)
        {
            return _options.DataSource;
        }

        if (_ownedDataSource is not null)
        {
            return _ownedDataSource;
        }

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string or NpgsqlDataSource was not configured.");
        }

        _ownedDataSource = NpgsqlDataSource.Create(BuildEffectiveConnectionString());
        return _ownedDataSource;
    }

    public RawSqlQuery<T> SqlQuery<T>(FormattableString sql) where T : class
    {
        return new RawSqlQuery<T>(this, sql);
    }

    public RawSqlCommand SqlCommand(FormattableString sql)
    {
        return new RawSqlCommand(this, sql);
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var models = GetEntityModels();
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                $"DbContext '{GetType().FullName}' does not have generated entity metadata. Ensure the Perigon.PostgreSQL source generator is referenced by the application.");
        }

        var tableModels = models.Where(static model => !model.IsView).ToArray();
        var schemas = tableModels
            .Select(model => model.Schema)
            .Where(static schema => !string.IsNullOrWhiteSpace(schema))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var foreignKeys = RelationshipConventions.InferForeignKeys(tableModels);

        if (_transactionConnection is not null && _transaction is not null)
        {
            foreach (var schema in schemas)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateSchema(schema!), _transactionConnection, _transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var model in tableModels)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateTable(model), _transactionConnection, _transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var foreignKey in foreignKeys)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildAddForeignKey(foreignKey), _transactionConnection, _transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var index in tableModels.SelectMany(static model => model.Indexes))
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateIndex(index), _transactionConnection, _transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var comment in tableModels.SelectMany(CreateTableSqlBuilder.BuildComments))
            {
                await ExecuteDdlAsync(comment, _transactionConnection, _transaction, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await using var connection = await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var schema in schemas)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateSchema(schema!), connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var model in tableModels)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateTable(model), connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var foreignKey in foreignKeys)
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildAddForeignKey(foreignKey), connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var index in tableModels.SelectMany(static model => model.Indexes))
            {
                await ExecuteDdlAsync(CreateTableSqlBuilder.BuildCreateIndex(index), connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            foreach (var comment in tableModels.SelectMany(CreateTableSqlBuilder.BuildComments))
            {
                await ExecuteDdlAsync(comment, connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public void EnsureCreated()
    {
        EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    protected virtual IReadOnlyList<EntityModel> GetEntityModels()
    {
        var models = EntityModelRegistry.GetContextModels(GetType());
        if (models.Count == 0)
        {
            return models;
        }

        var builder = new ModelBuilder();
        OnModelCreating(builder);
        return builder.Apply(models);
    }

    public async Task TransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        _ = await TransactionAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> TransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        await using var connection = await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _transactionConnection = connection;
        _transaction = transaction;

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _transaction = null;
            _transactionConnection = null;
        }
    }

    internal NpgsqlCommand CreateCommand(Sql.BoundSql sql)
    {
        var command = _transactionConnection?.CreateCommand() ?? GetDataSource().CreateCommand();
        command.CommandText = sql.CommandText;
        if (_options.CommandTimeout is not null)
        {
            command.CommandTimeout = TimeoutSeconds(_options.CommandTimeout.Value, nameof(_options.CommandTimeout));
        }

        if (_transaction is not null)
        {
            command.Transaction = _transaction;
        }

        foreach (var parameter in sql.Parameters)
        {
            var npgsqlParameter = command.CreateParameter();
            npgsqlParameter.Value = NormalizeParameterValue(parameter.Value) ?? DBNull.Value;
            if (parameter.DbType is not null)
            {
                npgsqlParameter.NpgsqlDbType = parameter.DbType.Value;
            }

            command.Parameters.Add(npgsqlParameter);
        }

        return command;
    }

    internal string BuildEffectiveConnectionString()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string was not configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString);
        if (_options.ConnectionTimeout is not null)
        {
            builder.Timeout = TimeoutSeconds(_options.ConnectionTimeout.Value, nameof(_options.ConnectionTimeout));
        }

        if (_options.CommandTimeout is not null)
        {
            builder.CommandTimeout = TimeoutSeconds(_options.CommandTimeout.Value, nameof(_options.CommandTimeout));
        }

        if (_options.PoolingEnabled is not null)
        {
            builder.Pooling = _options.PoolingEnabled.Value;
        }

        if (_options.MinPoolSize is not null)
        {
            builder.MinPoolSize = _options.MinPoolSize.Value;
        }

        if (_options.MaxPoolSize is not null)
        {
            builder.MaxPoolSize = _options.MaxPoolSize.Value;
        }

        return builder.ConnectionString;
    }

    private static object? NormalizeParameterValue(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            _ => value
        };
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

    private static async Task ExecuteDdlAsync(string commandText, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal bool TryGetTransactionConnection([NotNullWhen(true)] out NpgsqlConnection? connection)
    {
        connection = _transactionConnection;
        return connection is not null;
    }

    internal EntityModel ResolveEntityModel(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        return GetEntityModels().FirstOrDefault(item => item.ClrType == entityType)
            ?? EntityModel.For(entityType);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (_transactionConnection is not null)
        {
            await _transactionConnection.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownedDataSource is not null)
        {
            await _ownedDataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _transactionConnection?.Dispose();
        _ownedDataSource?.Dispose();
    }
}
