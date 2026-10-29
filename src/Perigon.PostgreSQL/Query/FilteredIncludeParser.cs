using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL.Query;

internal static class FilteredIncludeParser
{
    internal static MemberExpression ReadNavigation(Expression expression)
    {
        while (expression is MethodCallExpression call && call.Arguments.Count > 0 &&
               (call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(Queryable)))
            expression = call.Arguments[0];
        return expression as MemberExpression
            ?? throw new UnsupportedQueryExpressionException("Include requires a direct navigation property.");
    }

    internal static QueryModel Parse(Expression? expression, Type? childType, bool readValues = true)
    {
        var model = new QueryModel();
        if (expression is null or MemberExpression) return model;
        var calls = new List<MethodCallExpression>();
        var current = expression;
        while (current is MethodCallExpression call && call.Arguments.Count > 0 &&
               (call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(Queryable)))
        {
            calls.Add(call);
            current = call.Arguments[0];
        }
        if (current is not MemberExpression)
            throw new UnsupportedQueryExpressionException("Filtered Include requires a direct collection navigation.");
        calls.Reverse();
        var ordered = false;
        var paged = false;
        foreach (var call in calls)
        {
            switch (call.Method.Name)
            {
                case nameof(Enumerable.Where) when !paged && call.Arguments.Count == 2:
                    model.Predicates.Add(ReadLambda(call.Arguments[1], childType));
                    break;
                case nameof(Enumerable.OrderBy) or nameof(Enumerable.OrderByDescending) when !paged && !ordered && call.Arguments.Count == 2:
                    model.Orderings.Add(new OrderingModel(ReadLambda(call.Arguments[1], childType), call.Method.Name == nameof(Enumerable.OrderByDescending)));
                    ordered = true;
                    break;
                case nameof(Enumerable.ThenBy) or nameof(Enumerable.ThenByDescending) when !paged && ordered && call.Arguments.Count == 2:
                    model.Orderings.Add(new OrderingModel(ReadLambda(call.Arguments[1], childType), call.Method.Name == nameof(Enumerable.ThenByDescending)));
                    break;
                case nameof(Enumerable.Skip) when model.Skip is null && model.Take is null && call.Arguments.Count == 2:
                    model.Skip = readValues ? ReadCount(call.Arguments[1]) : 0;
                    paged = true;
                    break;
                case nameof(Enumerable.Take) when model.Take is null && call.Arguments.Count == 2:
                    model.Take = readValues ? ReadCount(call.Arguments[1]) : 0;
                    paged = true;
                    break;
                default:
                    throw new UnsupportedQueryExpressionException($"Filtered Include operator or order '{call.Method.Name}' is not supported.");
            }
        }
        if (paged && !ordered)
            throw new UnsupportedQueryExpressionException("Filtered Include Skip/Take requires OrderBy.");
        return model;
    }

    private static LambdaExpression ReadLambda(Expression expression, Type? childType)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote) expression = quote.Operand;
        if (expression is not LambdaExpression { Parameters.Count: 1 } lambda || (childType is not null && lambda.Parameters[0].Type != childType))
            throw new UnsupportedQueryExpressionException("Filtered Include requires a single-parameter child lambda.");
        return lambda;
    }

    private static int ReadCount(Expression expression)
    {
        if (ExpressionValueReader.Read(expression) is not int count)
            throw new UnsupportedQueryExpressionException("Filtered Include Skip/Take requires an integer value.");
        return Math.Max(0, count);
    }
}
