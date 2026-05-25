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

        var parentModel = EntityModel.For<TParent>();
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

        if (queryModel.Orderings.Count > 0)
        {
            var orderings = queryModel.Orderings.Select(o =>
                translator.TranslateMember(o.KeySelector) + (o.Descending ? " DESC" : " ASC"));
            sql += " ORDER BY " + string.Join(", ", orderings);
        }

        if (queryModel.Take is not null)
        {
            sql += " LIMIT " + parameters.Add(queryModel.Take.Value);
        }

        if (queryModel.Skip is not null)
        {
            sql += " OFFSET " + parameters.Add(queryModel.Skip.Value);
        }

        return CommandExecutor.ExecuteQueryAsync<TChild>(
            children.Context,
            children.Model,
            new BoundSql(sql, parameters.Parameters),
            cancellationToken);
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
