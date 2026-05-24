using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.RawSql;

internal static class RawSqlBuilder
{
    public static BoundSql Build(FormattableString sql)
    {
        var parameters = new List<SqlParameterValue>();
        var arguments = sql.GetArguments();
        for (var i = 0; i < arguments.Length; i++)
        {
            parameters.Add(new SqlParameterValue(i + 1, arguments[i]));
        }

        var placeholders = Enumerable.Range(1, arguments.Length)
            .Select(i => (object)("$" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var commandText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            sql.Format,
            placeholders);

        return new BoundSql(commandText, parameters);
    }
}
