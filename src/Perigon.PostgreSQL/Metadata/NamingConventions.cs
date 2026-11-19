using System.Text;
using Humanizer;

namespace Perigon.PostgreSQL.Metadata;

public static class NamingConventions
{
    public static string DefaultTableName(Type entityType)
    {
        var name = entityType.Name;
        var genericArity = name.IndexOf('`');
        if (genericArity >= 0)
        {
            name = name[..genericArity];
        }

        return name.Pluralize(inputIsKnownToBeSingular: false);
    }

    public static string DefaultColumnName(string propertyName)
    {
        return propertyName;
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
