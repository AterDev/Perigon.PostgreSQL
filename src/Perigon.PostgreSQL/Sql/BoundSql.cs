namespace Perigon.PostgreSQL.Sql;

public sealed class BoundSql
{
    public BoundSql(string commandText, IReadOnlyList<SqlParameterValue> parameters)
    {
        CommandText = commandText;
        Parameters = parameters;
    }

    public string CommandText { get; }

    public IReadOnlyList<SqlParameterValue> Parameters { get; }

    public override string ToString()
    {
        return CommandText;
    }
}
