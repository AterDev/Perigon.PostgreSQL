using Npgsql;
using System.Diagnostics.CodeAnalysis;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.RawSql;

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

    protected virtual void OnConfiguring(DbContextOptionsBuilder builder)
    {
    }

    protected DbSet<T> Set<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class
    {
        if (_sets.TryGetValue(typeof(T), out var existing))
        {
            return (DbSet<T>)existing;
        }

        var set = new DbSet<T>(this, EntityModel.For<T>());
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

        _ownedDataSource = NpgsqlDataSource.Create(_options.ConnectionString);
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
        if (_transaction is not null)
        {
            command.Transaction = _transaction;
        }

        foreach (var parameter in sql.Parameters)
        {
            var npgsqlParameter = command.CreateParameter();
            npgsqlParameter.Value = parameter.Value ?? DBNull.Value;
            if (parameter.DbType is not null)
            {
                npgsqlParameter.NpgsqlDbType = parameter.DbType.Value;
            }

            command.Parameters.Add(npgsqlParameter);
        }

        return command;
    }

    internal bool TryGetTransactionConnection([NotNullWhen(true)] out NpgsqlConnection? connection)
    {
        connection = _transactionConnection;
        return connection is not null;
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
