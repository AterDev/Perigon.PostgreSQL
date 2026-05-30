using NpgsqlTypes;
using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Sql;

internal static class InsertManySqlBuilder
{
    public static BoundSql Build<T>(EntityModel model, IReadOnlyList<T> entities, ReturningOptions<T>? options)
        where T : class
    {
        if (entities.Count == 0)
        {
            throw new InvalidOperationException("InsertManyReturning requires at least one entity.");
        }

        var parameters = new ParameterBag();
        var columns = model.Columns.Where(c => c.IsWritable).ToArray();
        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var rows = entities.Select(entity =>
            "(" + string.Join(", ", columns.Select(c => parameters.Add(EntityValueAccessorRegistry.GetValue(c, entity), InferDbType(c)))) + ")");
        var returningColumns = options?.ReturnAllColumns == false && model.PrimaryKeys.Count > 0
            ? model.PrimaryKeys
            : model.Columns;

        var sql = $"INSERT INTO {model.StoreObjectName} ({columnSql}) VALUES {string.Join(", ", rows)} RETURNING " +
                  string.Join(", ", returningColumns.Select(c => Identifier.Quote(c.ColumnName)));
        return new BoundSql(sql, parameters.Parameters);
    }

    public static BoundSql BuildNonReturning<T>(EntityModel model, IReadOnlyList<T> entities)
        where T : class
    {
        if (entities.Count == 0)
        {
            throw new InvalidOperationException("Bulk insert values requires at least one entity.");
        }

        var parameters = new ParameterBag();
        var columns = model.Columns.Where(c => c.IsWritable).ToArray();
        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var rows = entities.Select(entity =>
            "(" + string.Join(", ", columns.Select(c => parameters.Add(EntityValueAccessorRegistry.GetValue(c, entity), InferDbType(c)))) + ")");

        var sql = $"INSERT INTO {model.StoreObjectName} ({columnSql}) VALUES {string.Join(", ", rows)}";
        return new BoundSql(sql, parameters.Parameters);
    }

    private static NpgsqlDbType? InferDbType(ColumnModel column)
    {
        return column.TypeName?.Equals("jsonb", StringComparison.OrdinalIgnoreCase) == true
            ? NpgsqlDbType.Jsonb
            : null;
    }
}
