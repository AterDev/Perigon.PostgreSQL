using NpgsqlTypes;

namespace Perigon.PostgreSQL.Sql;

public sealed record SqlParameterValue(int Position, object? Value, NpgsqlDbType? DbType = null)
{
    public string Placeholder => "$" + Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
