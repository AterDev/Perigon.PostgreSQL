namespace Perigon.PostgreSQL.Expressions;

public sealed class UnsupportedQueryExpressionException : NotSupportedException
{
    public UnsupportedQueryExpressionException(string message)
        : base(message)
    {
    }
}
