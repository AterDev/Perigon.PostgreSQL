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
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            Visit(call.Arguments[0], model);

            switch (call.Method.Name)
            {
                case nameof(Queryable.Where):
                    model.Predicates.Add(UnquoteLambda(call.Arguments[1]));
                    return;
                case nameof(Queryable.OrderBy):
                    model.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), false));
                    return;
                case nameof(Queryable.OrderByDescending):
                    model.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), true));
                    return;
                case nameof(Queryable.ThenBy):
                    model.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), false));
                    return;
                case nameof(Queryable.ThenByDescending):
                    model.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), true));
                    return;
                case nameof(Queryable.Skip):
                    model.Skip = ReadInt(call.Arguments[1]);
                    return;
                case nameof(Queryable.Take):
                    model.Take = ReadInt(call.Arguments[1]);
                    return;
            }
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
