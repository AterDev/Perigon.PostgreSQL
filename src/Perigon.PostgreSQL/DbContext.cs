using Npgsql;
using System.Diagnostics.CodeAnalysis;
using System.Data;
using Microsoft.Extensions.Logging;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Migrations;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.RawSql;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL;

/// <summary>
/// Options for an explicit database transaction.
/// </summary>
public sealed class TransactionOptions
{
    /// <summary>
    /// Gets the PostgreSQL transaction isolation level. The default is
    /// <see cref="IsolationLevel.ReadCommitted"/>.
    /// </summary>
    public IsolationLevel IsolationLevel { get; init; } = IsolationLevel.ReadCommitted;
}

public abstract partial class DbContext : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<Type, object> _sets = [];
    private IReadOnlyList<EntityModel>? _entityModels;
    private Dictionary<Type, EntityModel>? _entityModelsByType;
    private ModelBuilder? _modelBuilder;
    private readonly DbContextOptions _options;
    private readonly object _dataSourceSync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AsyncLocal<OperationState?> _ambientOperation = new();
    private NpgsqlDataSource? _ownedDataSource;
    private NpgsqlConnection? _transactionConnection;
    private NpgsqlTransaction? _transaction;
    private int _disposed;
    private int _poolAttached;
    private bool _poolPoisoned;
    private long _poolLeaseVersion;

    protected DbContext()
    {
        var builder = new DbContextOptionsBuilder();
        OnConfiguring(builder);
        _options = builder.Build();
    }

    protected DbContext(Action<DbContextOptionsBuilder> configure)
    {
        var builder = new DbContextOptionsBuilder();
        ArgumentNullException.ThrowIfNull(configure);
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

    /// <summary>
    /// Resets application-owned state before this context is returned to a context pool.
    /// </summary>
    /// <remarks>
    /// The default implementation is empty. Override this hook when the context stores
    /// mutable state such as a tenant identifier. The hook must not start database
    /// operations or retain state that belongs to the previous lease.
    /// </remarks>
    protected virtual void OnPoolReturn()
    {
    }

    protected DbSet<T> Set<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class
    {
        ThrowIfDisposed();
        if (HasAmbientFilterScope)
            return new DbSet<T>(this, ResolveEntityModel(typeof(T)));
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
        ThrowIfDisposed();
        if (_options.DataSource is not null)
        {
            return _options.DataSource;
        }

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL connection string or NpgsqlDataSource was not configured.");
        }

        lock (_dataSourceSync)
        {
            ThrowIfDisposed();
            if (_ownedDataSource is null)
            {
                _ownedDataSource = NpgsqlDataSource.Create(BuildEffectiveConnectionString());
            }

            return _ownedDataSource;
        }
    }

    public RawSqlQuery<T> SqlQuery<T>(FormattableString sql) where T : class
    {
        ThrowIfDisposed();
        return new RawSqlQuery<T>(this, sql);
    }

    public RawSqlCommand SqlCommand(FormattableString sql)
    {
        ThrowIfDisposed();
        return new RawSqlCommand(this, sql);
    }

    /// <summary>
    /// Ensures that the configured database and the current context's schema exist.
    /// If the target database is missing, the configured role must be able to connect to a
    /// PostgreSQL maintenance database and create databases.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
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

        await using var connection = await OpenConnectionEnsuringDatabaseAsync(cancellationToken).ConfigureAwait(false);
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
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original DDL/cancellation exception.
            }

            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionEnsuringDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            await EnsureDatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
            return await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var connectionString = _options.ConnectionString ?? _options.DataSource?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "A PostgreSQL connection string is required to create a missing database."
            );
        }

        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = target.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "Set an explicit Database in the PostgreSQL connection string to create a missing database."
            );
        }

        var maintenanceDatabase = string.Equals(databaseName, "postgres", StringComparison.Ordinal)
            ? "template1"
            : "postgres";
        await using var maintenanceDataSource = NpgsqlDataSource.Create(
            _options.BuildEffectiveConnectionString(maintenanceDatabase)
        );
        await using var connection = await maintenanceDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @databaseName)";
        existsCommand.Parameters.AddWithValue("databaseName", databaseName);
        var exists = (bool)(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (exists)
        {
            return;
        }

        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE {QuoteDatabaseIdentifier(databaseName)}";
        try
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another application instance created the database after our existence check.
        }
    }

    private static string QuoteDatabaseIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public void EnsureCreated()
    {
        EnsureCreatedAsync().GetAwaiter().GetResult();
    }

    /// <summary>Returns the final schema model after source-generated metadata and OnModelCreating have been applied.</summary>
    public MigrationModel GetMigrationModel()
    {
        using var operation = EnterOperation();
        return MigrationModel.FromEntityModels(GetEntityModels());
    }

    public Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        return MigrateAsync(targetMigrationId: null, cancellationToken);
    }

    /// <summary>Applies or rolls back migrations until the database reaches the requested migration ID. Use "0" to roll back all migrations.</summary>
    public async Task MigrateAsync(string? targetMigrationId, CancellationToken cancellationToken = default)
    {
        await MigrateAsyncCore(targetMigrationId, dataSource: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies or rolls back migrations using the supplied data source instead of the context's configured connection.</summary>
    public async Task MigrateWithDataSourceAsync(
        NpgsqlDataSource dataSource,
        string? targetMigrationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await MigrateAsyncCore(targetMigrationId, dataSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateAsyncCore(
        string? targetMigrationId,
        NpgsqlDataSource? dataSource,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_transaction is not null)
        {
            throw new InvalidOperationException("Migrations cannot run inside a DbContext transaction.");
        }

        using var operation = EnterOperation();
        await MigrationRunner.MigrateAsync(this, targetMigrationId, dataSource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks the specified migration as applied after verifying that the managed PostgreSQL schema matches the supplied snapshot.</summary>
    public async Task BaselineAsync(
        string migrationId,
        MigrationModel expectedModel,
        CancellationToken cancellationToken = default)
    {
        await BaselineAsyncCore(migrationId, expectedModel, dataSource: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Baselines a database using the supplied data source instead of the context's configured connection.</summary>
    public async Task BaselineWithDataSourceAsync(
        string migrationId,
        MigrationModel expectedModel,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await BaselineAsyncCore(migrationId, expectedModel, dataSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task BaselineAsyncCore(
        string migrationId,
        MigrationModel expectedModel,
        NpgsqlDataSource? dataSource,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A migration baseline cannot run inside a DbContext transaction.");
        }

        using var operation = EnterOperation();
        await MigrationRunner.BaselineAsync(this, migrationId, expectedModel, dataSource, cancellationToken).ConfigureAwait(false);
    }

    protected virtual IReadOnlyList<EntityModel> GetEntityModels()
    {
        if (_entityModels is not null)
        {
            return _entityModels;
        }

        try
        {
            var models = EntityModelRegistry.GetContextModels(GetType());
            var builder = new ModelBuilder();
            OnModelCreating(builder);
            var configured = builder.Apply(models);
            var modelsByType = configured.ToDictionary(static model => model.ClrType);
            _modelBuilder = builder;
            _entityModels = configured;
            _entityModelsByType = modelsByType;
            return configured;
        }
        catch
        {
            if (Volatile.Read(ref _poolAttached) != 0)
                _poolPoisoned = true;
            throw;
        }
    }

    internal string MigrationHistorySchema => _options.MigrationHistorySchema;

    internal string MigrationHistoryTable => Identifier.Qualify(_options.MigrationHistorySchema, _options.MigrationHistoryTableName);

    public async Task TransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        await TransactionAsync(action, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an operation in a transaction using the supplied options.
    /// The cancellation token controls opening the connection, the action, and
    /// all commands in the action. Commit is completed with a non-cancellable
    /// token, so cancellation arriving after the action has completed does not
    /// interrupt the commit call. A connection failure during commit can still
    /// leave the final database outcome unknown.
    /// </summary>
    public async Task TransactionAsync(
        Func<CancellationToken, Task> action,
        TransactionOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = await TransactionAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> TransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        return await TransactionAsync(action, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an operation in a transaction using the supplied options.
    /// Nested transactions are not supported; an inner operation reuses the
    /// outer transaction when it is issued through the same context.
    /// </summary>
    public async Task<TResult> TransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        TransactionOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        options ??= new TransactionOptions();
        using var operation = EnterTransactionOperation();
        if (_transaction is not null)
        {
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        await using var connection = await GetDataSource().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(options.IsolationLevel, cancellationToken)
            .ConfigureAwait(false);
        _transactionConnection = connection;
        _transaction = transaction;

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            // Once the action has completed, do not let caller cancellation
            // interrupt the commit call. A connection failure can still leave
            // the final database outcome unknown.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original action/commit/cancellation exception.
            }

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
        ThrowIfDisposed();
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
        return _options.BuildEffectiveConnectionString();
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
        ThrowIfDisposed();
        connection = _transactionConnection;
        return connection is not null;
    }

    internal ILogger? Logger => _options.LoggerFactory?.CreateLogger("Perigon.PostgreSQL");

    internal bool SensitiveLoggingEnabled => _options.SensitiveLoggingEnabled;

    internal IReadOnlyList<EntityModel> GetIncludeEntityModels() => GetEntityModels();

    internal EntityModel ResolveEntityModel(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        ThrowIfDisposed();
        var models = GetEntityModels();
        var modelsByType = _entityModelsByType ??= models.ToDictionary(static model => model.ClrType);
        if (modelsByType.TryGetValue(entityType, out var configured))
        {
            return configured;
        }

        var builder = _modelBuilder;
        if (builder is null)
        {
            builder = new ModelBuilder();
            OnModelCreating(builder);
            _modelBuilder = builder;
        }

        var fallback = builder.Apply([EntityModel.For(entityType)])[0];
        modelsByType[entityType] = fallback;
        return fallback;
    }

    internal void AttachPool()
    {
        if (_sets.Count != 0 || _unfilteredSets.Count != 0 || _entityModels is not null ||
            _modelBuilder is not null || _queryFilters.Count != 0)
            throw new InvalidOperationException("A pooled context must not initialize models, filters or DbSets in its constructor; use expression-bodied DbSet properties.");
        Volatile.Write(ref _poolAttached, 1);
        Interlocked.Increment(ref _poolLeaseVersion);
    }

    internal void BeginPoolLease()
    {
        Interlocked.Increment(ref _poolLeaseVersion);
        Volatile.Write(ref _disposed, 0);
    }

    internal long CapturePoolLease() => Volatile.Read(ref _poolLeaseVersion);

    internal void ValidatePoolLease(long version)
    {
        if (Volatile.Read(ref _poolAttached) != 0 &&
            (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _poolLeaseVersion) != version))
            throw new ObjectDisposedException(GetType().Name, "The context pool lease that created this object has ended.");
    }

    internal bool ResetForPool()
    {
        if (!_operationGate.Wait(0))
            return false;

        try
        {
            if (_poolPoisoned || _transaction is not null || _transactionConnection is not null)
                return false;
            OnPoolReturn();
            if (this is IPooledDbContext legacyPoolContext)
                legacyPoolContext.ResetState();
            _sets.Clear();
            _unfilteredSets.Clear();
            _ambientOperation.Value = null;
            _ambientIgnoredFilters.Value = null;
            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal void DestroyPooledWhenIdle()
    {
        if (_operationGate.Wait(0))
        {
            _operationGate.Release();
            DestroyPooledResources();
            return;
        }

        _ = DestroyPooledWhenIdleAsync();
    }

    private async Task DestroyPooledWhenIdleAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
        DestroyPooledResources();
    }

    private void DestroyPooledResources()
    {
        _transaction?.Dispose();
        _transactionConnection?.Dispose();
        _ownedDataSource?.Dispose();
        _ownedDataSource = null;
        _operationGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _poolAttached) != 0)
            return;

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (_transactionConnection is not null)
        {
            await _transactionConnection.DisposeAsync().ConfigureAwait(false);
        }

        NpgsqlDataSource? ownedDataSource;
        lock (_dataSourceSync)
        {
            ownedDataSource = _ownedDataSource;
            _ownedDataSource = null;
        }

        if (ownedDataSource is not null)
        {
            await ownedDataSource.DisposeAsync().ConfigureAwait(false);
        }

        _operationGate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _poolAttached) != 0)
            return;

        _transaction?.Dispose();
        _transactionConnection?.Dispose();
        NpgsqlDataSource? ownedDataSource;
        lock (_dataSourceSync)
        {
            ownedDataSource = _ownedDataSource;
            _ownedDataSource = null;
        }

        ownedDataSource?.Dispose();
        _operationGate.Dispose();
    }

    internal IDisposable EnterOperation()
    {
        ThrowIfDisposed();
        var ambient = _ambientOperation.Value;
        if (ambient is not null && ReferenceEquals(ambient.Context, this))
        {
            ValidatePoolLease(ambient.LeaseVersion);
            if (!ambient.IsTransaction)
            {
                throw new InvalidOperationException(
                    "Concurrent operations on the same DbContext are not supported. Use a separate DbContext instance for parallel work.");
            }

            if (Interlocked.CompareExchange(ref ambient.ActiveCommand, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Concurrent operations inside the same transaction are not supported. Await each operation before starting the next one.");
            }

            return new OperationLease(this, ambient, reentrant: true);
        }

        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Concurrent operations on the same DbContext are not supported. Use a separate DbContext instance for parallel work.");
        }

        var state = new OperationState(this, isTransaction: false);
        _ambientOperation.Value = state;
        return new OperationLease(this, state, reentrant: false);
    }

    private IDisposable EnterTransactionOperation()
    {
        ThrowIfDisposed();
        if (_ambientOperation.Value is { } ambient)
        {
            ValidatePoolLease(ambient.LeaseVersion);
            throw new InvalidOperationException("Nested transactions are not supported.");
        }

        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Concurrent operations on the same DbContext are not supported. Use a separate DbContext instance for parallel work.");
        }

        var state = new OperationState(this, isTransaction: true);
        _ambientOperation.Value = state;
        return new OperationLease(this, state, reentrant: false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Volatile.Read(ref _poolAttached) != 0)
        {
            if (_ambientOperation.Value is { } operation)
                ValidatePoolLease(operation.LeaseVersion);
            _ = GetAmbientIgnoredFilters();
        }
    }

    private sealed class OperationState
    {
        public OperationState(DbContext context, bool isTransaction)
        {
            Context = context;
            IsTransaction = isTransaction;
            LeaseVersion = context.CapturePoolLease();
        }

        public DbContext Context { get; }

        public bool IsTransaction { get; }

        public long LeaseVersion { get; }

        public int ActiveCommand;
    }

    private sealed class OperationLease : IDisposable
    {
        private readonly DbContext _context;
        private readonly OperationState _state;
        private readonly bool _reentrant;
        private int _disposed;

        public OperationLease(DbContext context, OperationState state, bool reentrant)
        {
            _context = context;
            _state = state;
            _reentrant = reentrant;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_reentrant)
            {
                Volatile.Write(ref _state.ActiveCommand, 0);
                return;
            }

            _context._ambientOperation.Value = null;
            _context._operationGate.Release();
        }
    }
}
