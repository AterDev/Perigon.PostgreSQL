using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL.Query;

internal static class QueryModelFactory
{
    public static QueryModel Create(Expression expression)
    {
        var model = new QueryModel();
        Visit(expression, model);
        return model;
    }

    private static void Visit(Expression expression, QueryModel model)
    {
        if (expression is not MethodCallExpression call)
        {
            return;
        }

        if (call.Method.DeclaringType != typeof(Queryable))
        {
            throw new UnsupportedQueryExpressionException(
                $"Query method '{call.Method.DeclaringType?.FullName}.{call.Method.Name}' is not supported in SQL translation.");
        }

        Visit(call.Arguments[0], model);

        switch (call.Method.Name)
        {
            case nameof(Queryable.Where):
                model.AddPredicate(UnquoteLambda(call.Arguments[1]));
                return;
            case nameof(Queryable.OrderBy):
                model.AddOrdering(UnquoteLambda(call.Arguments[1]), descending: false, primary: true);
                return;
            case nameof(Queryable.OrderByDescending):
                model.AddOrdering(UnquoteLambda(call.Arguments[1]), descending: true, primary: true);
                return;
            case nameof(Queryable.ThenBy):
                model.AddOrdering(UnquoteLambda(call.Arguments[1]), descending: false, primary: false);
                return;
            case nameof(Queryable.ThenByDescending):
                model.AddOrdering(UnquoteLambda(call.Arguments[1]), descending: true, primary: false);
                return;
            case nameof(Queryable.Skip):
                model.AddSkip(ReadInt(call.Arguments[1]));
                return;
            case nameof(Queryable.Take):
                model.AddTake(ReadInt(call.Arguments[1]));
                return;
            default:
                throw new UnsupportedQueryExpressionException(
                    $"Queryable operator '{call.Method.Name}' is not supported in SQL translation.");
        }
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Quote } unary)
        {
            expression = unary.Operand;
        }

        return expression as LambdaExpression
            ?? throw new NotSupportedException($"Expression '{expression}' is not a lambda.");
    }

    private static int ReadInt(Expression expression)
    {
        if (expression is ConstantExpression { Value: int value })
        {
            return value;
        }

        var evaluated = ExpressionValueReader.Read(expression);
        return evaluated is int valueFromExpression
            ? valueFromExpression
            : throw new NotSupportedException("Skip/Take require integer values.");
    }
}
