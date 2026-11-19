using System.Globalization;

namespace Perigon.PostgreSQL.Sql;

internal static class EnumStorage
{
    public static object? ConvertValue(object? value, string? storeType = null)
    {
        if (value is not Enum enumValue)
        {
            return value;
        }

        return storeType?.Trim().ToLowerInvariant() switch
        {
            null or "" or "smallint" or "int2" => (object)Convert.ToInt16(enumValue, CultureInfo.InvariantCulture),
            "integer" or "int" or "int4" => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture),
            "bigint" or "int8" => Convert.ToInt64(enumValue, CultureInfo.InvariantCulture),
            _ => value
        };
    }
}
