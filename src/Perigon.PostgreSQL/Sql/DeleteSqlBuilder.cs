using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Sql;

internal static class DeleteSqlBuilder
{
    public static BoundSql Build(EntityModel model, QueryModel queryModel, bool allowFullTableDelete)
    {
        if (queryModel.Predicates.Count == 0 && !allowFullTableDelete)
        {
            throw new InvalidOperationException(
                "Refusing to execute DELETE without WHERE. Pass options that explicitly allow full-table delete.");
        }

        var parameters = new ParameterBag();
        const string alias = "e";
        var sql = $"DELETE FROM {model.StoreObjectName} AS {alias}";
        var translator = new SqlExpressionTranslator(model, parameters, alias);

        if (queryModel.Predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", queryModel.Predicates.Select(translator.TranslatePredicate));
        }

        return new BoundSql(sql, parameters.Parameters);
    }
}
