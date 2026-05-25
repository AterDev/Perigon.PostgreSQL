using System.Linq.Expressions;
using NpgsqlTypes;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Bulk;

namespace Perigon.PostgreSQL.Sql;

internal static class UpsertSqlBuilder
{
    public static BoundSql Build<T>(
        EntityModel model,
        IReadOnlyList<T> entities,
        Expression<Func<T, object>> conflictKey,
        UpsertOptions<T>? options)
        where T : class
    {
        if (entities.Count == 0)
        {
            throw new InvalidOperationException("Upsert requires at least one entity.");
        }

        var parameters = new ParameterBag();
        var columns = model.Columns.Where(c => c.IsWritable).ToArray();
        var conflictColumns = ReadConflictColumns(model, conflictKey.Body);
        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var rows = entities.Select(entity =>
            "(" + string.Join(", ", columns.Select(c => parameters.Add(EntityValueAccessorRegistry.GetValue(c, entity), InferDbType(c)))) + ")");

        var sql = $"INSERT INTO {model.StoreObjectName} ({columnSql}) VALUES {string.Join(", ", rows)} ON CONFLICT ({string.Join(", ", conflictColumns.Select(Identifier.Quote))})";
        if (options?.DoNothing == true)
        {
            sql += " DO NOTHING";
            return new BoundSql(sql, parameters.Parameters);
        }

        var updateColumns = columns
            .Where(c => !conflictColumns.Contains(c.ColumnName, StringComparer.Ordinal))
            .ToArray();
        if (updateColumns.Length == 0)
        {
            sql += " DO NOTHING";
            return new BoundSql(sql, parameters.Parameters);
        }

        sql += " DO UPDATE SET " + string.Join(", ", updateColumns.Select(c =>
            $"{Identifier.Quote(c.ColumnName)} = EXCLUDED.{Identifier.Quote(c.ColumnName)}"));
        return new BoundSql(sql, parameters.Parameters);
    }

    private static IReadOnlyList<string> ReadConflictColumns<T>(EntityModel model, Expression<Func<T, object>> selector)
    {
        return ReadConflictColumns(model, selector.Body);
    }

    private static IReadOnlyList<string> ReadConflictColumns(EntityModel model, Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member)
        {
            return [model.GetColumn(member.Member.Name).ColumnName];
        }

        if (expression is NewExpression @new)
        {
            return @new.Arguments
                .Select(StripConvert)
                .OfType<MemberExpression>()
                .Select(m => model.GetColumn(m.Member.Name).ColumnName)
                .ToArray();
        }

        throw new UnsupportedQueryExpressionException($"Conflict key expression '{expression}' is not supported.");
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        return expression;
    }

    private static NpgsqlDbType? InferDbType(ColumnModel column)
    {
        return column.TypeName?.Equals("jsonb", StringComparison.OrdinalIgnoreCase) == true
            ? NpgsqlDbType.Jsonb
            : null;
    }
}
