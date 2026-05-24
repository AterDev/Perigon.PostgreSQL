using Perigon.PostgreSQL.Metadata;
using NpgsqlTypes;

namespace Perigon.PostgreSQL.Sql;

internal static class InsertSqlBuilder
{
    public static BoundSql Build(EntityModel model, object entity, bool returning)
    {
        var parameters = new ParameterBag();
        var columns = model.Columns.Where(c => c.IsWritable).ToArray();
        if (columns.Length == 0)
        {
            throw new InvalidOperationException($"Entity '{model.ClrType.Name}' does not have writable columns.");
        }

        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var valueSql = string.Join(", ", columns.Select(c => parameters.Add(c.Property.GetValue(entity), InferDbType(c))));
        var sql = $"INSERT INTO {model.StoreObjectName} ({columnSql}) VALUES ({valueSql})";

        if (returning)
        {
            sql += " RETURNING " + string.Join(", ", model.Columns.Select(c => Identifier.Quote(c.ColumnName)));
        }

        return new BoundSql(sql, parameters.Parameters);
    }

    private static NpgsqlDbType? InferDbType(ColumnModel column)
    {
        return column.TypeName?.Equals("jsonb", StringComparison.OrdinalIgnoreCase) == true
            ? NpgsqlDbType.Jsonb
            : null;
    }
}
