using System.Linq.Expressions;
using System.Reflection;

namespace Perigon.PostgreSQL.Expressions;

public static class ExpressionValueReader
{
    public static object? Read(Expression expression)
    {
        expression = StripConvert(expression);

        return expression switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression member => ReadMember(member),
            NewArrayExpression array => array.Expressions.Select(Read).ToArray(),
            MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion => Read(conversion.Arguments[0]),
            _ => throw new UnsupportedQueryExpressionException(
                $"Expression '{expression}' cannot be evaluated as a SQL parameter. Use a captured variable or constant.")
        };
    }

    private static object? ReadMember(MemberExpression member)
    {
        var instance = member.Expression is null ? null : Read(member.Expression);
        return member.Member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => throw new UnsupportedQueryExpressionException(
                $"Member '{member.Member.Name}' cannot be evaluated as a SQL parameter.")
        };
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion)
        {
            return StripConvert(conversion.Arguments[0]);
        }

        return expression;
    }
}
