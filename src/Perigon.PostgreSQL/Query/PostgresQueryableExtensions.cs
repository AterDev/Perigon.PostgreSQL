using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;
using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Update;

namespace Perigon.PostgreSQL;

public static class PostgresQueryableExtensions
{
    public static BoundSql ToSql(this IQueryable source)
    {
        return StructuredSqlBuilder.Build(source);
    }

    public static BoundSql ToQuerySql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source)
        where T : class
    {
        return source.ToSql();
    }

    public static BoundSql ToInsertSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        T entity,
        bool returning = true)
        where T : class
    {
        return InsertSqlBuilder.Build(set.Model, entity, returning);
    }

    public static BoundSql ToCountSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source)
        where T : class
    {
        return AggregateSqlBuilder.BuildCount(ResolveEntityModel(source), QueryModelFactory.Create(source.Expression));
    }

    public static BoundSql ToLongCountSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source)
        where T : class
    {
        return source.ToCountSql();
    }

    public static BoundSql ToAnySql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source)
        where T : class
    {
        return AggregateSqlBuilder.BuildAny(ResolveEntityModel(source), QueryModelFactory.Create(source.Expression));
    }

    public static BoundSql ToDeleteSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        DeleteOptions? options = null)
        where T : class
    {
        return DeleteSqlBuilder.Build(
            ResolveEntityModel(source),
            QueryModelFactory.Create(source.Expression),
            options?.AllowFullTableDelete == true);
    }

    public static BoundSql ToUpdateSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        Expression<Func<UpdateSetters<T>, UpdateSetters<T>>> setters,
        UpdateOptions? options = null)
        where T : class
    {
        return UpdateSqlBuilder.Build(
            ResolveEntityModel(source),
            QueryModelFactory.Create(source.Expression),
            setters,
            options?.AllowFullTableUpdate == true);
    }

    public static Task<List<T>> ToListAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        var context = GetContext(source);
        return CommandExecutor.ExecuteQueryAsync<T>(
            context,
            ResolveEntityModel(source),
            source.ToQuerySql(),
            cancellationToken);
    }

    public static Task<List<T>> ToScalarListAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
    {
        return CommandExecutor.ExecuteScalarListAsync<T>(
            GetContext(source),
            source.ToSql(),
            cancellationToken);
    }

    public static async Task<T?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        return (await source.Take(1).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    public static async Task<T?> SingleOrDefaultAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        var results = await source.Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        return results.Count switch
        {
            0 => null,
            1 => results[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element.")
        };
    }

    public static Task<int> CountAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return CommandExecutor.ExecuteScalarAsync<int>(GetContext(source), source.ToCountSql(), cancellationToken);
    }

    public static Task<long> LongCountAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return CommandExecutor.ExecuteScalarAsync<long>(GetContext(source), source.ToLongCountSql(), cancellationToken);
    }

    public static async Task<bool> AnyAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var value = await CommandExecutor.ExecuteScalarAsync<int?>(GetContext(source), source.ToAnySql(), cancellationToken)
            .ConfigureAwait(false);
        return value is not null;
    }

    public static Task<T> InsertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        T entity,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        return InsertReturningAsync(set, entity, cancellationToken);
    }

    public static Task<int> InsertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        T entity,
        bool returning,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return CommandExecutor.ExecuteNonQueryAsync(set.Context, set.ToInsertSql(entity, returning), cancellationToken);
    }

    public static Task<int> ExecuteDeleteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        DeleteOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return CommandExecutor.ExecuteNonQueryAsync(GetContext(source), source.ToDeleteSql(options), cancellationToken);
    }

    public static Task<int> ExecuteUpdateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this IQueryable<T> source,
        Expression<Func<UpdateSetters<T>, UpdateSetters<T>>> setters,
        UpdateOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return CommandExecutor.ExecuteNonQueryAsync(GetContext(source), source.ToUpdateSql(setters, options), cancellationToken);
    }

    public static Task BulkInsertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities,
        BulkInsertOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        options ??= new BulkInsertOptions();
        return options.Mode == BulkInsertMode.InsertValues
            ? BulkInsertValuesAsync(set, entities, options, cancellationToken)
            : BulkCopyExecutor.BulkInsertAsync(set, entities, cancellationToken);
    }

    public static BoundSql ToBulkInsertValuesSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities)
        where T : class
    {
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        return InsertManySqlBuilder.BuildNonReturning(set.Model, rows);
    }

    public static BoundSql ToInsertManyReturningSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities,
        ReturningOptions<T>? options = null)
        where T : class
    {
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        return InsertManySqlBuilder.Build(set.Model, rows, options);
    }

    public static Task<List<T>> InsertManyReturningAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities,
        ReturningOptions<T>? options = null,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        if (rows.Count == 0)
        {
            return Task.FromResult(new List<T>());
        }

        return InsertManyReturningBatchedAsync(set, rows, options ?? new ReturningOptions<T>(), cancellationToken);
    }

    public static BoundSql ToUpsertSql<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities,
        Expression<Func<T, object>> conflictKey,
        UpsertOptions<T>? options = null)
        where T : class
    {
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        return UpsertSqlBuilder.Build(set.Model, rows, conflictKey, options);
    }

    public static Task<int> UpsertManyAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this DbSet<T> set,
        IEnumerable<T> entities,
        Expression<Func<T, object>> conflictKey,
        UpsertOptions<T>? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        if (rows.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CommandExecutor.ExecuteNonQueryAsync(set.Context, set.ToUpsertSql(rows, conflictKey, options), cancellationToken);
    }

    private static DbContext GetContext<T>(IQueryable<T> source)
    {
        if (source.Provider is PostgresQueryProvider provider)
        {
            return provider.Context;
        }

        throw new InvalidOperationException("Query was not created by Perigon.PostgreSQL.");
    }

    private static EntityModel ResolveEntityModel<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(IQueryable<T> source)
    {
        if (source is IEntityModelSource modelSource)
        {
            return modelSource.Model;
        }

        return GetContext(source).ResolveEntityModel(typeof(T));
    }

    private static async Task BulkInsertValuesAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        DbSet<T> set,
        IEnumerable<T> entities,
        BulkInsertOptions options,
        CancellationToken cancellationToken)
        where T : class
    {
        if (options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than zero.");
        }

        var rows = entities as IReadOnlyList<T> ?? entities.ToArray();
        if (rows.Count == 0)
        {
            return;
        }

        for (var offset = 0; offset < rows.Count; offset += options.BatchSize)
        {
            var batch = rows.Skip(offset).Take(options.BatchSize).ToArray();
            await CommandExecutor.ExecuteNonQueryAsync(
                set.Context,
                set.ToBulkInsertValuesSql(batch),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> InsertReturningAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        DbSet<T> set,
        T entity,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        var results = await CommandExecutor.ExecuteQueryAsync<T>(
            set.Context,
            set.Model,
            set.ToInsertSql(entity),
            cancellationToken).ConfigureAwait(false);

        return results.Count == 1
            ? results[0]
            : throw new InvalidOperationException($"INSERT RETURNING expected one row but received {results.Count}.");
    }

    private static async Task<List<T>> InsertManyReturningBatchedAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        DbSet<T> set,
        IReadOnlyList<T> rows,
        ReturningOptions<T> options,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        if (options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than zero.");
        }

        var results = new List<T>(rows.Count);
        for (var offset = 0; offset < rows.Count; offset += options.BatchSize)
        {
            var batch = rows.Skip(offset).Take(options.BatchSize).ToArray();
            var inserted = await CommandExecutor.ExecuteQueryAsync<T>(
                set.Context,
                set.Model,
                set.ToInsertManyReturningSql(batch, options),
                cancellationToken).ConfigureAwait(false);
            results.AddRange(inserted);
        }

        return results;
    }
}
