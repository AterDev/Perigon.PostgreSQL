using System.Collections;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL.Query;

internal interface IIncludedQuery<T> where T : class
{
    DbContext Context { get; }
    Task LoadIncludesAsync(List<T> entities, CancellationToken cancellationToken);
}

internal sealed class IncludedQueryable<T> : IOrderedQueryable<T>, IIncludedQuery<T> where T : class
{
    private readonly IReadOnlyDictionary<string, Func<DbContext, List<T>, CancellationToken, Task>> _loaders;

    internal IncludedQueryable(PostgresQueryProvider provider, Expression expression,
        IReadOnlyDictionary<string, Func<DbContext, List<T>, CancellationToken, Task>> loaders)
    {
        Expression = expression;
        _loaders = loaders;
        Provider = new IncludeQueryProvider<T>(provider, loaders);
        Context = provider.Context;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    public DbContext Context { get; }
    internal IReadOnlyDictionary<string, Func<DbContext, List<T>, CancellationToken, Task>> Loaders => _loaders;
    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Synchronous query execution is not supported.");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async Task LoadIncludesAsync(List<T> entities, CancellationToken cancellationToken)
    {
        if (entities.Count == 0) return;
        foreach (var loader in _loaders.Values)
            await loader(Context, entities, cancellationToken).ConfigureAwait(false);
    }
}

internal interface IIncludeProvider { DbContext Context { get; } }

internal sealed class IncludeQueryProvider<T> : IQueryProvider, IIncludeProvider where T : class
{
    private readonly PostgresQueryProvider _inner;
    private readonly IReadOnlyDictionary<string, Func<DbContext, List<T>, CancellationToken, Task>> _loaders;
    internal IncludeQueryProvider(PostgresQueryProvider inner, IReadOnlyDictionary<string, Func<DbContext, List<T>, CancellationToken, Task>> loaders)
    {
        _inner = inner;
        _loaders = loaders;
    }
    internal PostgresQueryProvider Inner => _inner;
    public DbContext Context => _inner.Context;
    public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException("Non-generic query creation is not supported in NativeAOT mode.");
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) != typeof(T) || expression is not MethodCallExpression call ||
            call.Method.DeclaringType != typeof(Queryable) || call.Method.Name is not
                (nameof(Queryable.Where) or nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or
                 nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) or nameof(Queryable.Skip) or nameof(Queryable.Take)))
            throw new UnsupportedQueryExpressionException("Only filtering, ordering, and paging are supported after Include.");
        return (IQueryable<TElement>)(object)new IncludedQueryable<T>(_inner, expression, _loaders);
    }
    public object? Execute(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported.");
    public TResult Execute<TResult>(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported.");
}
