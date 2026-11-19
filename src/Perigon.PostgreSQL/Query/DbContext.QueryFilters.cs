using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

public abstract partial class DbContext : IQueryFilterContext
{
    private readonly Dictionary<Type, Dictionary<string, LambdaExpression>> _queryFilters = [];
    private readonly List<QueryFilterTemplate> _queryFilterTemplates = [];
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

        RegisterQueryFilter(typeof(TEntity), name, predicate);
    }

    /// <summary>
    /// Registers a named filter for every mapped entity that derives from or implements
    /// <typeparamref name="TBase"/>. The predicate is adapted to each concrete entity
    /// type when a query is created, so the registration remains NativeAOT-safe and does
    /// not require runtime generic reflection.
    /// </summary>
    protected void EnableSoftDeleteFilter<TBase>(
        string name,
        Expression<Func<TBase, bool>> predicate)
        where TBase : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);

        if (Volatile.Read(ref _poolAttached) != 0)
            new PooledFilterValidator(this).Visit(predicate);

        var propertyName = ReadSoftDeletePropertyName(predicate)
            ?? throw new ArgumentException(
                "Soft-delete filters must be a negated boolean property, for example entity => !entity.IsDeleted.",
                nameof(predicate));
        _queryFilterTemplates.Add(new QueryFilterTemplate(typeof(TBase), name, propertyName, predicate));
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
        _queryFilters.TryGetValue(entityType, out var filters);
        var expression = root;
        if (filters is not null)
        {
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
        }

        foreach (var template in _queryFilterTemplates.Where(template => template.BaseType == entityType))
        {
            if (IsFilterIgnored(template.Name, ignoredFilters) || filters?.ContainsKey(template.Name) == true)
            {
                continue;
            }

            expression = Expression.Call(
                typeof(Queryable),
                nameof(Queryable.Where),
                [entityType],
                expression,
                Expression.Quote(template.Predicate));
        }

        return expression;
    }

    internal Expression ApplyQueryFilters<TEntity>(
        Expression root,
        IReadOnlySet<string>? ignoredFilters = null)
        where TEntity : class
    {
        _queryFilters.TryGetValue(typeof(TEntity), out var filters);
        var expression = root;
        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (IsFilterIgnored(filter.Key, ignoredFilters))
                {
                    continue;
                }

                expression = Expression.Call(
                    QueryableMethods<TEntity>.Where,
                    expression,
                    Expression.Quote(filter.Value));
            }
        }

        foreach (var template in _queryFilterTemplates)
        {
            if (!template.BaseType.IsAssignableFrom(typeof(TEntity)) ||
                filters?.ContainsKey(template.Name) == true ||
                IsFilterIgnored(template.Name, ignoredFilters))
            {
                continue;
            }

            var predicate = AdaptFilter<TEntity>(template);
            expression = Expression.Call(
                QueryableMethods<TEntity>.Where,
                expression,
                Expression.Quote(predicate));
        }

        return expression;
    }

    internal void ApplyQueryFilters(
        Type entityType,
        QueryModel queryModel,
        IReadOnlySet<string>? ignoredFilters = null)
    {
        _queryFilters.TryGetValue(entityType, out var filters);
        foreach (var filter in EnumerateFilters(entityType, filters))
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
        _queryFilters.TryGetValue(entityType, out var filters);
        var known = EnumerateFilters(entityType, filters)
            .Select(static filter => filter.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (known.Count == 0)
        {
            throw new InvalidOperationException($"Entity '{entityType.FullName}' has no configured query filters.");
        }

        var unknown = names.Where(name => !known.Contains(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown query filter(s) for '{entityType.Name}': {string.Join(", ", unknown)}.",
                nameof(names));
        }
    }

    private void RegisterQueryFilter(Type entityType, string name, LambdaExpression predicate)
    {
        if (!_queryFilters.TryGetValue(entityType, out var filters))
        {
            filters = new Dictionary<string, LambdaExpression>(StringComparer.Ordinal);
            _queryFilters.Add(entityType, filters);
        }

        filters[name] = predicate;
    }

    private IEnumerable<KeyValuePair<string, LambdaExpression>> EnumerateFilters(
        Type entityType,
        IReadOnlyDictionary<string, LambdaExpression>? filters)
    {
        var directNames = filters is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : filters.Keys.ToHashSet(StringComparer.Ordinal);

        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                yield return filter;
            }
        }

        foreach (var template in _queryFilterTemplates)
        {
            if (!template.BaseType.IsAssignableFrom(entityType) || directNames.Contains(template.Name))
            {
                continue;
            }

            yield return new KeyValuePair<string, LambdaExpression>(
                template.Name,
                template.Predicate);
        }
    }

    private static Expression<Func<TEntity, bool>> AdaptFilter<TEntity>(QueryFilterTemplate template)
        where TEntity : class
    {
        if (template.BaseType == typeof(TEntity))
        {
            return (Expression<Func<TEntity, bool>>)template.Predicate;
        }

        var parameter = Expression.Parameter(typeof(TEntity), template.Predicate.Parameters[0].Name);
        var body = new ParameterReplaceVisitor(template.Predicate.Parameters[0], parameter).Visit(template.Predicate.Body)
            ?? throw new InvalidOperationException("Query filter predicate has no body.");
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private sealed class ParameterReplaceVisitor(ParameterExpression source, ParameterExpression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? replacement : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == source)
            {
                return Expression.MakeMemberAccess(replacement, node.Member);
            }

            return base.VisitMember(node);
        }
    }

    private IReadOnlyList<Metadata.EntityModel> ApplySoftDeleteIndexConventions(
        IReadOnlyList<Metadata.EntityModel> models)
    {
        if (_queryFilterTemplates.Count == 0)
        {
            return models;
        }

        var configured = new List<Metadata.EntityModel>(models.Count);
        foreach (var model in models)
        {
            var template = _queryFilterTemplates.FirstOrDefault(candidate =>
                candidate.BaseType.IsAssignableFrom(model.ClrType));
            var softDeleteColumn = template is null
                ? null
                : model.Columns.FirstOrDefault(column =>
                    column.PropertyName == template.PropertyName && column.ClrType == typeof(bool));
            var uniqueIndexes = softDeleteColumn is null
                ? []
                : model.Indexes.Where(static index => index.IsUnique).ToArray();
            if (softDeleteColumn is null || uniqueIndexes.Length == 0)
            {
                configured.Add(model);
                continue;
            }

            var activeFilter = $"{Sql.Identifier.Quote(softDeleteColumn.ColumnName)} = false";
            var indexes = model.Indexes.Select(index => new Metadata.IndexDefinition(
                index.ConfiguredName,
                index.Columns.Select(column => column.PropertyName).ToArray(),
                index.IsUnique,
                index.IncludeColumns.Select(column => column.PropertyName).ToArray(),
                index.IsUnique
                    ? ComposeIndexFilter(index.Filter, activeFilter)
                    : index.Filter,
                index.Method)).ToArray();
            configured.Add(model.Apply(
                model.Schema,
                model.TableName,
                model.Columns,
                model.Comment,
                indexes,
                model.ForeignKeys));
        }

        return configured;
    }

    private static string? ReadSoftDeletePropertyName<TEntity>(Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        if (predicate.Body is UnaryExpression { NodeType: ExpressionType.Not, Operand: MemberExpression member } &&
            member.Expression == predicate.Parameters[0] &&
            member.Member is System.Reflection.PropertyInfo property &&
            property.PropertyType == typeof(bool))
        {
            return property.Name;
        }

        return null;
    }

    private static string ComposeIndexFilter(string? existingFilter, string activeFilter)
    {
        return string.IsNullOrWhiteSpace(existingFilter)
            ? activeFilter
            : $"{activeFilter} AND ({existingFilter})";
    }

    private static class QueryableMethods<TEntity>
        where TEntity : class
    {
        public static MethodInfo Where { get; } =
            ((Func<IQueryable<TEntity>, Expression<Func<TEntity, bool>>, IQueryable<TEntity>>)Queryable.Where).Method;
    }

    private sealed record QueryFilterTemplate(Type BaseType, string Name, string PropertyName, LambdaExpression Predicate);

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
