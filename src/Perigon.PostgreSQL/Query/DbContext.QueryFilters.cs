using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

public abstract partial class DbContext : IQueryFilterContext
{
    private readonly Dictionary<Type, Dictionary<string, LambdaExpression>> _queryFilters = [];
    private readonly Dictionary<Type, object> _unfilteredSets = [];
    private readonly AsyncLocal<FilterState?> _ambientIgnoredFilters = new();

    internal bool HasAmbientFilterScope => GetAmbientIgnoredFilters() is not null;

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

        if (Volatile.Read(ref _poolAttached) != 0)
            new PooledFilterValidator(this).Visit(predicate);

        if (!_queryFilters.TryGetValue(typeof(TEntity), out var filters))
        {
            filters = new Dictionary<string, LambdaExpression>(StringComparer.Ordinal);
            _queryFilters.Add(typeof(TEntity), filters);
        }

        filters[name] = predicate;
    }

    private sealed class PooledFilterValidator(DbContext context) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
        {
            var value = node.Value;
            if (value is not null && !ReferenceEquals(value, context) &&
                !node.Type.IsPrimitive && !node.Type.IsEnum &&
                value is not (string or decimal or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly))
                throw new InvalidOperationException(
                    "Pooled query filters cannot capture local objects or values. Reference resettable context properties directly instead.");
            return base.VisitConstant(node);
        }
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
        ThrowIfDisposed();
        _ = GetAmbientIgnoredFilters();
        var previous = _ambientIgnoredFilters.Value;
        var lease = CapturePoolLease();
        var state = new FilterState(new HashSet<string>(ignoredFilters, StringComparer.Ordinal), lease, previous);
        _ambientIgnoredFilters.Value = state;
        return new FilterScope(() =>
        {
            Interlocked.Exchange(ref state.Ended, 1);
            if (ReferenceEquals(_ambientIgnoredFilters.Value, state) &&
                (Volatile.Read(ref _poolAttached) == 0 ||
                 (Volatile.Read(ref _disposed) == 0 && CapturePoolLease() == lease)))
            {
                var restore = previous;
                while (restore is not null && Volatile.Read(ref restore.Ended) != 0)
                    restore = restore.Previous;
                _ambientIgnoredFilters.Value = restore;
            }
        });
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

    private IReadOnlySet<string>? GetAmbientIgnoredFilters()
    {
        var state = _ambientIgnoredFilters.Value;
        if (state is null)
            return null;
        for (var current = state; current is not null; current = current.Previous)
        {
            ValidatePoolLease(current.LeaseVersion);
            if (Volatile.Read(ref current.Ended) != 0)
                throw new InvalidOperationException("The query filter scope has ended.");
        }
        return state.Filters;
    }

    internal object? CaptureFilterScope()
    {
        _ = GetAmbientIgnoredFilters();
        return _ambientIgnoredFilters.Value;
    }

    internal void ValidateFilterScope(object? capturedScope)
    {
        if (capturedScope is not FilterState expected)
            return;
        _ = GetAmbientIgnoredFilters();
        for (var current = _ambientIgnoredFilters.Value; current is not null; current = current.Previous)
        {
            if (ReferenceEquals(current, expected))
                return;
        }
        throw new InvalidOperationException("A query created in a filter scope cannot be used outside that scope.");
    }

    private bool IsFilterIgnored(string name, IReadOnlySet<string>? explicitFilters)
    {
        _ = GetAmbientIgnoredFilters();
        if (explicitFilters?.Contains(name) == true || explicitFilters?.Contains("*") == true)
            return true;
        for (var current = _ambientIgnoredFilters.Value; current is not null; current = current.Previous)
            if (current.Filters.Contains(name) || current.Filters.Contains("*"))
                return true;
        return false;
    }

    private sealed class FilterState(IReadOnlySet<string> filters, long leaseVersion, FilterState? previous)
    {
        public IReadOnlySet<string> Filters { get; } = filters;
        public long LeaseVersion { get; } = leaseVersion;
        public FilterState? Previous { get; } = previous;
        public int Ended;
    }

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
