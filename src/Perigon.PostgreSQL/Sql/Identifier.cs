namespace Perigon.PostgreSQL.Sql;

public static class Identifier
{
    public static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(value));
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string Qualify(string? schema, string table)
    {
        return string.IsNullOrWhiteSpace(schema)
            ? Quote(table)
            : $"{Quote(schema)}.{Quote(table)}";
    }
}
