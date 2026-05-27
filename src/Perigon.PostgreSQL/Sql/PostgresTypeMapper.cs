using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Sql;

internal static class PostgresTypeMapper
{
    public static string Map(ColumnModel column)
    {
        if (!string.IsNullOrWhiteSpace(column.TypeName))
        {
            return column.TypeName!;
        }

        if (column.IsArray)
        {
            return MapArray(column.ClrType);
        }

        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (type == typeof(short)) return "smallint";
        if (type == typeof(int)) return "integer";
        if (type == typeof(long)) return "bigint";
        if (type == typeof(float)) return "real";
        if (type == typeof(double)) return "double precision";
        if (type == typeof(decimal)) return MapNumeric(column);
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(string)) return column.MaxLength is > 0 ? $"character varying({column.MaxLength.Value})" : "text";
        if (type == typeof(DateTime)) return MapTimestamp(column);
        if (type == typeof(DateTimeOffset)) return MapTimestamp(column);
        if (type == typeof(Guid)) return "uuid";
        if (type == typeof(byte[])) return "bytea";

        throw new NotSupportedException($"CLR type '{column.ClrType.FullName}' is not supported for PostgreSQL DDL generation. Configure an explicit TypeName.");
    }

    private static string MapArray(Type clrType)
    {
        var elementType = clrType.IsArray
            ? clrType.GetElementType()
            : clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>)
                ? clrType.GetGenericArguments()[0]
                : null;

        if (elementType is null)
        {
            throw new NotSupportedException($"CLR type '{clrType.FullName}' is not a supported PostgreSQL array type.");
        }

        var actual = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (actual == typeof(short)) return "smallint[]";
        if (actual == typeof(int)) return "integer[]";
        if (actual == typeof(long)) return "bigint[]";
        if (actual == typeof(float)) return "real[]";
        if (actual == typeof(double)) return "double precision[]";
        if (actual == typeof(decimal)) return "numeric[]";
        if (actual == typeof(bool)) return "boolean[]";
        if (actual == typeof(string)) return "text[]";
        if (actual == typeof(DateTime)) return "timestamp with time zone[]";
        if (actual == typeof(DateTimeOffset)) return "timestamp with time zone[]";
        if (actual == typeof(Guid)) return "uuid[]";

        throw new NotSupportedException($"Array element type '{actual.FullName}' is not supported for PostgreSQL DDL generation. Configure an explicit TypeName.");
    }

    private static string MapNumeric(ColumnModel column)
    {
        if (column.Precision is null)
        {
            return "numeric";
        }

        return column.Scale is null
            ? $"numeric({column.Precision.Value})"
            : $"numeric({column.Precision.Value},{column.Scale.Value})";
    }

    private static string MapTimestamp(ColumnModel column)
    {
        return column.Precision is null
            ? "timestamp with time zone"
            : $"timestamp({column.Precision.Value}) with time zone";
    }
}