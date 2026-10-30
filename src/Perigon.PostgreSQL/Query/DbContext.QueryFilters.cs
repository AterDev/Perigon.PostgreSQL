using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

public abstract partial class DbContext : IQueryFilterContext
{
    private readonly Dictionary<Type, Dictionary<string, LambdaExpression>> _queryFilters = [];
    private readonly Dictionary<Type, object> _unfilteredSets = [];
    private readonly AsyncLocal<IReadOnlySet<string>?> _ambientIgnoredFilters = new();

    internal bool HasAmbientFilterScope => _ambientIgnoredFilters.Value is not null;

    /// <summary>
    /// Registers a named predicate that is applied to every query root for the entity.
    /// The predicate remains an expression tree and is translated by the normal SQL
    /// pipeline; it is never evaluated on the client.
    /// </summary>
    protected void HasQueryFilter<TEntity>(
        string name,
        Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);

        if (!_queryFilters.TryGetValue(typeof(TEntity), out var filters))
        {
            filters = new Dictionary<string, LambdaExpression>(StringComparer.Ordinal);
            _queryFilters.Add(typeof(TEntity), filters);
        }

        filters[name] = predicate;
    }

    /// <summary>
    /// Creates an entity set with the selected named filters disabled.
    /// The returned set is independent from the filtered set cache.
    /// </summary>
    public DbSet<TEntity> SetWithoutFilters<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEntity>(
        params string[] ignoredFilters)
        where TEntity : class
    {
        ThrowIfDisposed();
        if (ignoredFilters is null or { Length: 0 })
        {
            throw new ArgumentException(
                "Specify at least one named filter. Use SetIgnoringAllFilters when all filters must be disabled.",
                nameof(ignoredFilters));
        }

        _ = ResolveEntityModel(typeof(TEntity));
        var ignored = new HashSet<string>(ignoredFilters, StringComparer.Ordinal);
        ValidateFilterNames(typeof(TEntity), ignored);
        return new DbSet<TEntity>(
            this,
            ResolveEntityModel(typeof(TEntity)),
            ignored);
    }

    /// <summary>
    /// Creates an entity set with every configured filter disabled. This is an
    /// intentionally separate API so an empty or misspelled filter list cannot
    /// silently widen a query.
    /// </summary>
    public DbSet<TEntity> SetIgnoringAllFilters<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEntity>()
        where TEntity : class
    {
        ThrowIfDisposed();
        if (_unfilteredSets.TryGetValue(typeof(TEntity), out var existing))
        {
            return (DbSet<TEntity>)existing;
        }

        var unfiltered = new DbSet<TEntity>(this, ResolveEntityModel(typeof(TEntity)), applyFilters: false);
        _unfilteredSets.Add(typeof(TEntity), unfiltered);
        return unfiltered;
    }

    Expression IQueryFilterContext.ApplyQueryFilters(Type entityType, Expression root)
        => ApplyQueryFilters(entityType, root, new HashSet<string>(StringComparer.Ordinal));

    Expression IQueryFilterContext.ApplyQueryFilters(
        Type entityType,
        Expression root,
        IReadOnlySet<string> ignoredFilters)
        => ApplyQueryFilters(entityType, root, ignoredFilters);

    IDisposable IQueryFilterContext.PushIgnoredFilters(IReadOnlySet<string> ignoredFilters)
    {
        ArgumentNullException.ThrowIfNull(ignoredFilters);
        var previous = _ambientIgnoredFilters.Value;
        _ambientIgnoredFilters.Value = ignoredFilters;
        return new FilterScope(() => _ambientIgnoredFilters.Value = previous);
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The expression is only constructed for SQL translation; it is never compiled or executed as a delegate.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2087",
        Justification = "The source-generated context has preserved public entity properties; the runtime type is the registered entity type.")]
    internal Expression ApplyQueryFilters(
        Type entityType,
        Expression root,
        IReadOnlySet<string>? ignoredFilters = null)
    {
        if (!_queryFilters.TryGetValue(entityType, out var filters))
        {
            return root;
        }

        var expression = root;
        foreach (var filter in filters)
        {
            if (IsFilterIgnored(filter.Key, ignoredFilters))
            {
                continue;
            }

            expression = Expression.Call(
                typeof(Queryable),
                nameof(Queryable.Where),
                [entityType],
                expression,
                Expression.Quote(filter.Value));
        }

        return expression;
    }

    internal void ApplyQueryFilters(
        Type entityType,
        QueryModel queryModel,
        IReadOnlySet<string>? ignoredFilters = null)
    {
        if (!_queryFilters.TryGetValue(entityType, out var filters))
        {
            return;
        }

        foreach (var filter in filters)
        {
            if (IsFilterIgnored(filter.Key, ignoredFilters))
            {
                continue;
            }

            queryModel.Predicates.Add(filter.Value);
        }
    }

    private bool IsFilterIgnored(string name, IReadOnlySet<string>? explicitFilters)
        => explicitFilters?.Contains(name) == true || explicitFilters?.Contains("*") == true
            || _ambientIgnoredFilters.Value?.Contains(name) == true || _ambientIgnoredFilters.Value?.Contains("*") == true;

    private void ValidateFilterNames(Type entityType, IReadOnlySet<string> names)
    {
        if (!_queryFilters.TryGetValue(entityType, out var filters))
        {
            throw new InvalidOperationException($"Entity '{entityType.FullName}' has no configured query filters.");
        }

        var unknown = names.Where(name => !filters.ContainsKey(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown query filter(s) for '{entityType.Name}': {string.Join(", ", unknown)}.",
                nameof(names));
        }
    }

    private sealed class FilterScope(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
