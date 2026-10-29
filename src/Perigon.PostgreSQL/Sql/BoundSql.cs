namespace Perigon.PostgreSQL.Sql;

public sealed class BoundSql
{
    public BoundSql(
        string commandText,
        IReadOnlyList<SqlParameterValue> parameters,
        IReadOnlyList<string>? nonNullableAggregateColumns = null)
    {
        CommandText = commandText;
        Parameters = parameters;
        NonNullableAggregateColumns = nonNullableAggregateColumns ?? Array.Empty<string>();
    }

    public string CommandText { get; }

    public IReadOnlyList<SqlParameterValue> Parameters { get; }

    internal IReadOnlyList<string> NonNullableAggregateColumns { get; }

    public override string ToString()
    {
        return CommandText;
    }
}
