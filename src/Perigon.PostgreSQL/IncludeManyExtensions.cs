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
        var parentRows = await parents.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (parentRows.Count == 0)
        {
            return [];
        }

        var parentModel = ResolveEntityModel(parents);
        var parentColumn = parentModel.GetColumn(ReadPropertyName(parentKey.Body));
        var childColumn = children.Model.GetColumn(ReadPropertyName(childForeignKey.Body));
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
    {
        var parameters = new ParameterBag();
        var alias = "e";
        var selectColumns = string.Join(", ", children.Model.Columns.Select(c => $"{alias}.{Identifier.Quote(c.ColumnName)}"));
        var sql = $"SELECT {selectColumns} FROM {children.Model.StoreObjectName} AS {alias} WHERE {alias}.{Identifier.Quote(column.ColumnName)} = ANY({parameters.Add(keys)})";
        var queryModel = childQuery is null
            ? new QueryModel()
            : QueryModelFactory.Create(childQuery(children).Expression);
        var translator = new SqlExpressionTranslator(children.Model, parameters, alias);
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

            return CommandExecutor.ExecuteQueryAsync<TChild>(
                children.Context,
                children.Model,
                new BoundSql(sql, parameters.Parameters),
                cancellationToken);
        }

        if (queryModel.Orderings.Count == 0)
        {
            throw new UnsupportedQueryExpressionException(
                "IncludeMany Skip/Take requires at least one OrderBy so each parent receives deterministic child pagination.");
        }

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
                : $"{skipParameter} + {parameters.Add(queryModel.Take.Value)}";
            sql += queryModel.Skip is null
                ? $" WHERE {outerAlias}.{Identifier.Quote(rowNumberAlias)} <= {upperBound}"
                : $" AND {outerAlias}.{Identifier.Quote(rowNumberAlias)} <= {upperBound}";
        }

        sql += $" ORDER BY {outerAlias}.{Identifier.Quote(column.ColumnName)}, {outerAlias}.{Identifier.Quote(rowNumberAlias)}";

        return CommandExecutor.ExecuteQueryAsync<TChild>(
            children.Context,
            children.Model,
            new BoundSql(sql, parameters.Parameters),
            cancellationToken);
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

    private static string ReadPropertyName(Expression expression)
    {
        expression = StripConvert(expression);
        return expression is MemberExpression { Member: System.Reflection.PropertyInfo property }
            ? property.Name
            : throw new NotSupportedException($"Include key expression '{expression}' must be a mapped property.");
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
