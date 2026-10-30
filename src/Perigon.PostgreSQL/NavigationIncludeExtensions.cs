using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

public static class NavigationIncludeExtensions
{
    /// <summary>Loads a generated navigation using a split query when the entity query is executed.</summary>
    public static IQueryable<TEntity> Include<TEntity, TProperty>(
        this IQueryable<TEntity> source, Expression<Func<TEntity, TProperty>> navigation)
        where TEntity : class
    {
        var member = FilteredIncludeParser.ReadNavigation(navigation.Body);
        if (member.Member is not System.Reflection.PropertyInfo property ||
            member.Expression != navigation.Parameters[0])
            throw new NotSupportedException("Include requires a direct navigation property expression.");
        _ = FilteredIncludeParser.Parse(navigation.Body, childType: null, readValues: false);

        var root = source.Expression;
        while (root is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable) &&
            call.Method.Name is nameof(Queryable.Where) or nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or
                nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) or nameof(Queryable.Skip) or nameof(Queryable.Take))
            root = call.Arguments[0];
        if (root is not ConstantExpression { Value: IEntityModelSource })
            throw new UnsupportedQueryExpressionException("Include requires an entity query with filtering, ordering, or paging.");

        var inner = source.Provider switch
        {
            PostgresQueryProvider provider => provider,
            IncludeQueryProvider<TEntity> provider => provider.Inner,
            _ => throw new InvalidOperationException("Query was not created by Perigon.PostgreSQL.")
        };
        var loader = NavigationIncludeRegistry.GetLoader<TEntity>(inner.Context.GetType(), property.Name);
        var loaders = source is IncludedQueryable<TEntity> included
            ? included.Loaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, Func<DbContext, List<TEntity>, CancellationToken, Task>>(StringComparer.Ordinal);
        if (loaders.ContainsKey(property.Name))
            throw new UnsupportedQueryExpressionException("Repeated Include on the same navigation is not supported.");
        var ignoredFilters = root is ConstantExpression { Value: IQueryFilterSource filterSource }
            ? filterSource.IgnoredFilters
            : new HashSet<string>(StringComparer.Ordinal);
        loaders[property.Name] = async (context, entities, cancellationToken) =>
        {
            using var scope = context is IQueryFilterContext filters
                ? filters.PushIgnoredFilters(ignoredFilters)
                : null;
            await loader(context, entities, navigation.Body, cancellationToken).ConfigureAwait(false);
        };
        return new IncludedQueryable<TEntity>(inner, source.Expression, loaders);
    }
}
