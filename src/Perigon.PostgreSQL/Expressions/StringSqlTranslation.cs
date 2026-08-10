using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Expressions;

internal static class StringSqlTranslation
{
    public static bool IsExactInstanceMethod(
        MethodCallExpression methodCall,
        string methodName,
        params Type[] parameterTypes)
    {
        if (methodCall.Object is null ||
            methodCall.Method.DeclaringType != typeof(string) ||
            methodCall.Method.Name != methodName ||
            methodCall.Arguments.Count != parameterTypes.Length)
        {
            return false;
        }

        return methodCall.Arguments.Select(static argument => argument.Type)
                   .SequenceEqual(parameterTypes);
    }

    public static string ReadLikePattern(Expression expression, string prefix, string suffix)
    {
        var value = ExpressionValueReader.Read(expression);
        if (value is not string text)
        {
            throw new UnsupportedQueryExpressionException(
                "String pattern arguments must be non-null string values.");
        }

        return prefix + EscapeLikePattern(text) + suffix;
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
