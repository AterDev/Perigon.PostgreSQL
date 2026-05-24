using System.Text;

namespace Perigon.PostgreSQL.Metadata;

public static class NamingConventions
{
    public static string DefaultTableName(Type entityType)
    {
        var name = ToSnakeCase(entityType.Name);
        return name.EndsWith('s') ? name : name + "s";
    }

    public static string DefaultColumnName(string propertyName)
    {
        return ToSnakeCase(propertyName);
    }

    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]) ||
                              (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
