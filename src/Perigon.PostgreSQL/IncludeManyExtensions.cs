using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL;

public static class IncludeManyExtensions
{
    /// <summary>Loads a many-to-many relationship through an explicitly mapped link entity using split queries.</summary>
    public static Task<List<TResult>> IncludeManyToManyAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TLink,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TParentKey, TChildKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TLink> links,
        DbSet<TChild> children,
        Expression<Func<TParent, TParentKey>> parentKey,
        Expression<Func<TLink, TParentKey>> linkParentKey,
        Expression<Func<TLink, TChildKey>> linkChildKey,
        Expression<Func<TChild, TChildKey>> childKey,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TLink : class, new()
        where TChild : class, new()
        where TParentKey : notnull
        where TChildKey : notnull
        => IncludeManyToManyAsync(parents, links, children, parentKey, linkParentKey, linkChildKey,
            childKey, childFilter: null, resultSelector, cancellationToken);

    /// <summary>Loads related children matching a server-side filter through an explicitly mapped link entity.</summary>
    public static async Task<List<TResult>> IncludeManyToManyAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TLink,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TParentKey, TChildKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TLink> links,
        DbSet<TChild> children,
        Expression<Func<TParent, TParentKey>> parentKey,
        Expression<Func<TLink, TParentKey>> linkParentKey,
        Expression<Func<TLink, TChildKey>> linkChildKey,
        Expression<Func<TChild, TChildKey>> childKey,
        Expression<Func<TChild, bool>>? childFilter,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TLink : class, new()
        where TChild : class, new()
        where TParentKey : notnull
        where TChildKey : notnull
    {
        if (parents.Provider is not PostgresQueryProvider provider ||
            !ReferenceEquals(provider.Context, links.Context) || !ReferenceEquals(provider.Context, children.Context))
        {
            throw new InvalidOperationException("Many-to-many loading requires all queries to use the same DbContext.");
        }

        var parentColumn = ResolveEntityModel(parents).GetColumn(ReadDirectPropertyName(parentKey));
        var linkParentColumn = links.Model.GetColumn(ReadDirectPropertyName(linkParentKey));
        var linkChildColumn = links.Model.GetColumn(ReadDirectPropertyName(linkChildKey));
        var childColumn = children.Model.GetColumn(ReadDirectPropertyName(childKey));
        var parentRows = await parents.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (parentRows.Count == 0)
        {
            return [];
        }

        var parentKeys = parentRows.Select(parent => (TParentKey?)EntityValueAccessorRegistry.GetValue(parentColumn, parent))
            .Where(key => key is not null).Select(key => key!).Distinct().ToArray();
        var linkRows = parentKeys.Length == 0 ? [] : await LoadChildrenAsync(
            links, linkParentColumn, parentKeys, null, cancellationToken).ConfigureAwait(false);
        var childKeys = linkRows.Select(link => (TChildKey?)EntityValueAccessorRegistry.GetValue(linkChildColumn, link))
            .Where(key => key is not null).Select(key => key!).Distinct().ToArray();
        var childRows = childKeys.Length == 0 ? [] : await LoadChildrenAsync(
            children, childColumn, childKeys, childFilter is null ? null : query => query.Where(childFilter), cancellationToken).ConfigureAwait(false);

        var childLookup = childRows.ToLookup(child => (TChildKey?)EntityValueAccessorRegistry.GetValue(childColumn, child));
        var linkLookup = linkRows.ToLookup(link => (TParentKey?)EntityValueAccessorRegistry.GetValue(linkParentColumn, link));
        var results = new List<TResult>(parentRows.Count);
        foreach (var parent in parentRows)
        {
            var key = (TParentKey?)EntityValueAccessorRegistry.GetValue(parentColumn, parent);
            var matches = new List<TChild>();
            if (key is not null)
            {
                foreach (var link in linkLookup[key])
                {
                    var linkedKey = (TChildKey?)EntityValueAccessorRegistry.GetValue(linkChildColumn, link);
                    if (linkedKey is not null)
                    {
                        matches.AddRange(childLookup[linkedKey]);
                    }
                }
            }

            results.Add(resultSelector(parent, matches));
        }

        return results;
    }

    public static async Task<List<TResult>> IncludeManyAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey,
        TResult>(
        this IQueryable<TParent> parents,
        DbSet<TChild> children,
        Expression<Func<TParent, TKey>> parentKey,
        Expression<Func<TChild, TKey>> childForeignKey,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TChild : class, new()
        where TKey : notnull
    {
        return await parents.IncludeManyAsync(
            children,
            parentKey,
            childForeignKey,
            childQuery: null,
            resultSelector,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<TResult>> IncludeManyAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey,
        TResult>(
        this IQueryable<TParent> parents,
        DbSet<TChild> children,
        Expression<Func<TParent, TKey>> parentKey,
        Expression<Func<TChild, TKey>> childForeignKey,
        Func<IQueryable<TChild>, IQueryable<TChild>>? childQuery,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TChild : class, new()
        where TKey : notnull
    {
        if (parents.Provider is not PostgresQueryProvider provider ||
            !ReferenceEquals(provider.Context, children.Context))
        {
            throw new InvalidOperationException("IncludeMany loading requires parent and child queries to use the same DbContext.");
        }

        var parentModel = ResolveEntityModel(parents);
        var parentColumn = parentModel.GetColumn(ReadDirectPropertyName(parentKey));
        var childColumn = children.Model.GetColumn(ReadDirectPropertyName(childForeignKey));
        var parentRows = await parents.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (parentRows.Count == 0)
        {
            return [];
        }

        var keys = parentRows
            .Select(parent => (TKey?)EntityValueAccessorRegistry.GetValue(parentColumn, parent))
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct()
            .ToArray();

        var childRows = keys.Length == 0
            ? []
            : await LoadChildrenAsync(children, childColumn, keys, childQuery, cancellationToken).ConfigureAwait(false);

        var childLookup = childRows
            .GroupBy(child => (TKey?)EntityValueAccessorRegistry.GetValue(childColumn, child))
            .Where(group => group.Key is not null)
            .ToDictionary(group => group.Key!, group => group.ToList());

        var results = new List<TResult>(parentRows.Count);
        foreach (var parent in parentRows)
        {
            var key = (TKey?)EntityValueAccessorRegistry.GetValue(parentColumn, parent);
            var matchingChildren = key is not null && childLookup.TryGetValue(key, out var grouped)
                ? grouped
                : [];
            results.Add(resultSelector(parent, matchingChildren));
        }

        return results;
    }

    private static Task<List<TChild>> LoadChildrenAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey>(
        DbSet<TChild> children,
        ColumnModel column,
        TKey[] keys,
        Func<IQueryable<TChild>, IQueryable<TChild>>? childQuery,
        CancellationToken cancellationToken)
        where TChild : class, new()
        where TKey : notnull
        => CommandExecutor.ExecuteQueryAsync<TChild>(children.Context, children.Model,
            BuildChildrenSql(children, column, keys, childQuery), cancellationToken);

    internal static BoundSql BuildChildrenSql<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey>(
        DbSet<TChild> children,
        ColumnModel column,
        TKey[] keys,
        Func<IQueryable<TChild>, IQueryable<TChild>>? childQuery)
        where TChild : class, new()
        where TKey : notnull
    {
        if (childQuery is null)
        {
            var queryModel = new QueryModel();
            children.Context.ApplyQueryFilters(typeof(TChild), queryModel);
            return BuildChildrenSql(children, column, keys, queryModel, useCSharpNullSemantics: false, applyGlobalFilters: false);
        }

        return BuildChildrenSql(
            children,
            column,
            keys,
            QueryModelFactory.Create(childQuery(children).Expression),
            useCSharpNullSemantics: false,
            applyGlobalFilters: false);
    }

    internal static BoundSql BuildChildrenSql<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey>(DbSet<TChild> children, ColumnModel column, TKey[] keys, QueryModel queryModel, bool useCSharpNullSemantics = false, bool applyGlobalFilters = true)
        where TChild : class, new()
        where TKey : notnull
    {
        if (applyGlobalFilters)
        {
            children.Context.ApplyQueryFilters(typeof(TChild), queryModel);
        }

        var parameters = new ParameterBag();
        var alias = "e";
        var selectColumns = string.Join(", ", children.Model.Columns.Select(c => $"{alias}.{Identifier.Quote(c.ColumnName)}"));
        var sql = $"SELECT {selectColumns} FROM {children.Model.StoreObjectName} AS {alias} WHERE {alias}.{Identifier.Quote(column.ColumnName)} = ANY({parameters.Add(keys)})";
        var translator = new SqlExpressionTranslator(children.Model, parameters, alias, useCSharpNullSemantics: useCSharpNullSemantics);
        foreach (var predicate in queryModel.Predicates)
        {
            sql += " AND " + translator.TranslatePredicate(predicate);
        }

        var orderingsSql = queryModel.Orderings.Count == 0
            ? null
            : string.Join(", ", queryModel.Orderings.Select(o =>
                translator.TranslateMember(o.KeySelector) + (o.Descending ? " DESC" : " ASC")));

        if (queryModel.Take is null && queryModel.Skip is null)
        {
            if (orderingsSql is not null)
            {
                sql += " ORDER BY " + orderingsSql;
            }

            return new BoundSql(sql, parameters.Parameters);
        }

        if (queryModel.Orderings.Count == 0)
        {
            throw new UnsupportedQueryExpressionException(
                "IncludeMany Skip/Take requires at least one OrderBy so each parent receives deterministic child pagination.");
        }

        if (children.Model.PrimaryKeys.Count == 0)
            throw new UnsupportedQueryExpressionException("IncludeMany Skip/Take requires a child primary key for stable pagination.");
        orderingsSql += ", " + string.Join(", ", children.Model.PrimaryKeys.Select(key =>
            $"{alias}.{Identifier.Quote(key.ColumnName)} ASC"));

        var rowNumberAlias = "__perigon_include_row";
        var innerSql = sql.Insert(
            sql.IndexOf(" FROM ", StringComparison.Ordinal),
            $", row_number() OVER (PARTITION BY {alias}.{Identifier.Quote(column.ColumnName)} ORDER BY {orderingsSql}) AS {Identifier.Quote(rowNumberAlias)}");
        var outerAlias = "included";
        var outerColumns = string.Join(", ", children.Model.Columns.Select(c =>
            $"{outerAlias}.{Identifier.Quote(c.ColumnName)}"));
        sql = $"SELECT {outerColumns} FROM ({innerSql}) AS {outerAlias}";

        string? skipParameter = null;
        if (queryModel.Skip is not null)
        {
            skipParameter = parameters.Add(queryModel.Skip.Value);
            sql += $" WHERE {outerAlias}.{Identifier.Quote(rowNumberAlias)} > {skipParameter}";
        }

        if (queryModel.Take is not null)
        {
            var upperBound = queryModel.Skip is null
                ? parameters.Add(queryModel.Take.Value)
                : $"({skipParameter})::bigint + {parameters.Add(queryModel.Take.Value)}";
            sql += queryModel.Skip is null
                ? $" WHERE {outerAlias}.{Identifier.Quote(rowNumberAlias)} <= {upperBound}"
                : $" AND {outerAlias}.{Identifier.Quote(rowNumberAlias)} <= {upperBound}";
        }

        sql += $" ORDER BY {outerAlias}.{Identifier.Quote(column.ColumnName)}, {outerAlias}.{Identifier.Quote(rowNumberAlias)}";

        return new BoundSql(sql, parameters.Parameters);
    }

    private static EntityModel ResolveEntityModel<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(IQueryable<T> source)
    {
        if (source is IEntityModelSource modelSource)
        {
            return modelSource.Model;
        }

        if (source.Provider is PostgresQueryProvider provider)
        {
            return provider.Context.ResolveEntityModel(typeof(T));
        }

        throw new InvalidOperationException("Query was not created by Perigon.PostgreSQL.");
    }

    private static string ReadDirectPropertyName(LambdaExpression expression)
    {
        var body = StripConvert(expression.Body);
        return body is MemberExpression { Member: System.Reflection.PropertyInfo property } member &&
               member.Expression == expression.Parameters[0]
            ? property.Name
            : throw new NotSupportedException($"Include key expression '{expression}' must be a direct mapped property.");
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        return expression;
    }
}
