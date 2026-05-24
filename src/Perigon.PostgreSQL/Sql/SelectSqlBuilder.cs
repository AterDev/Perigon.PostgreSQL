using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Sql;

internal static class SelectSqlBuilder
{
    public static BoundSql Build(EntityModel model, QueryModel queryModel)
    {
        var parameters = new ParameterBag();
        const string alias = "e";
        var columns = string.Join(", ", model.Columns.Select(c => $"{alias}.{Identifier.Quote(c.ColumnName)}"));
        var sql = $"SELECT {columns} FROM {model.StoreObjectName} AS {alias}";
        var translator = new SqlExpressionTranslator(model, parameters, alias);

        if (queryModel.Predicates.Count > 0)
        {
            var where = queryModel.Predicates.Select(translator.TranslatePredicate);
            sql += " WHERE " + string.Join(" AND ", where);
        }

        if (queryModel.Orderings.Count > 0)
        {
            var orderings = queryModel.Orderings.Select(o =>
                translator.TranslateMember(o.KeySelector) + (o.Descending ? " DESC" : " ASC"));
            sql += " ORDER BY " + string.Join(", ", orderings);
        }

        if (queryModel.Take is not null)
        {
            sql += " LIMIT " + parameters.Add(queryModel.Take.Value);
        }

        if (queryModel.Skip is not null)
        {
            sql += " OFFSET " + parameters.Add(queryModel.Skip.Value);
        }

        return new BoundSql(sql, parameters.Parameters);
    }
}
