using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class AutoProjectionExpression : Expression
{
    public AutoProjectionExpression(
        Expression sourceExpression,
        Type queryType,
        IReadOnlyList<AutoProjectionProperty> properties)
    {
        SourceExpression = sourceExpression;
        QueryType = queryType;
        Properties = properties;
    }

    public Expression SourceExpression { get; }

    public Type QueryType { get; }

    public IReadOnlyList<AutoProjectionProperty> Properties { get; }

    public override ExpressionType NodeType => ExpressionType.Extension;

    public override Type Type => QueryType;

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visitedSource = visitor.Visit(SourceExpression)
            ?? throw new InvalidOperationException("An automatic projection source cannot be null.");
        return visitedSource == SourceExpression
            ? this
            : new AutoProjectionExpression(visitedSource, QueryType, Properties);
    }
}

internal sealed record AutoProjectionProperty(string SourcePropertyName, string TargetPropertyName);
