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
        var conflictColumns = ReadConflictColumns(model, conflictKey.Body);
        var conflictColumnModels = conflictColumns
            .Select(name => model.Columns.First(column => string.Equals(column.ColumnName, name, StringComparison.Ordinal)))
            .ToArray();
        foreach (var conflictColumn in conflictColumnModels)
        {
            if (!conflictColumn.IsWritable && !conflictColumn.IsIdentity)
            {
                throw new UnsupportedQueryExpressionException(
                    $"Generated column '{conflictColumn.PropertyName}' cannot be used as an upsert conflict key.");
            }
        }

        var columns = model.Columns
            .Where(c => c.IsWritable || conflictColumns.Contains(c.ColumnName, StringComparer.Ordinal))
            .ToArray();
        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var rows = entities.Select(entity =>
            "(" + string.Join(", ", columns.Select(c => parameters.Add(EntityValueAccessorRegistry.GetValue(c, entity), InferDbType(c)))) + ")");

        var overridingIdentity = conflictColumnModels.Any(static column => column.IsIdentity);
        var sql = $"INSERT INTO {model.StoreObjectName} ({columnSql}){(overridingIdentity ? " OVERRIDING SYSTEM VALUE" : string.Empty)} VALUES {string.Join(", ", rows)} ON CONFLICT ({string.Join(", ", conflictColumns.Select(Identifier.Quote))})";
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
            if (@new.Arguments.Count == 0)
            {
                throw new UnsupportedQueryExpressionException("Conflict key expression must select at least one mapped property.");
            }

            var columns = new List<string>(@new.Arguments.Count);
            foreach (var argument in @new.Arguments)
            {
                var selectedMember = StripConvert(argument) as MemberExpression
                    ?? throw new UnsupportedQueryExpressionException(
                        $"Conflict key expression '{argument}' must select mapped properties only.");
                columns.Add(model.GetColumn(selectedMember.Member.Name).ColumnName);
            }

            if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
            {
                throw new UnsupportedQueryExpressionException("Conflict key expression cannot select the same property more than once.");
            }

            return columns;
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
