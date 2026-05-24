using NpgsqlTypes;

namespace Perigon.PostgreSQL.Sql;

internal sealed class ParameterBag
{
    private readonly List<SqlParameterValue> _parameters = [];

    public ParameterBag()
    {
    }

    public ParameterBag(IEnumerable<SqlParameterValue> parameters)
    {
        _parameters.AddRange(parameters);
    }

    public IReadOnlyList<SqlParameterValue> Parameters => _parameters;

    public string Add(object? value, NpgsqlDbType? dbType = null)
    {
        var parameter = new SqlParameterValue(_parameters.Count + 1, value, dbType);
        _parameters.Add(parameter);
        return parameter.Placeholder;
    }
}
