using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Sql;

internal static class AggregateSqlBuilder
{
    public static BoundSql BuildCount(EntityModel model, QueryModel queryModel)
    {
        return BuildScalar(model, queryModel, "count(*)");
    }

    public static BoundSql BuildAny(EntityModel model, QueryModel queryModel)
    {
        var parameters = new ParameterBag();
        const string alias = "e";
        var sql = $"SELECT 1 FROM {model.StoreObjectName} AS {alias}";
        AppendWhere(model, queryModel, parameters, alias, ref sql);
        sql += " LIMIT 1";
        return new BoundSql(sql, parameters.Parameters);
    }

    private static BoundSql BuildScalar(EntityModel model, QueryModel queryModel, string scalar)
    {
        var parameters = new ParameterBag();
        const string alias = "e";
        var sql = $"SELECT {scalar} FROM {model.StoreObjectName} AS {alias}";
        AppendWhere(model, queryModel, parameters, alias, ref sql);
        return new BoundSql(sql, parameters.Parameters);
    }

    private static void AppendWhere(
        EntityModel model,
        QueryModel queryModel,
        ParameterBag parameters,
        string alias,
        ref string sql)
    {
        if (queryModel.Predicates.Count == 0)
        {
            return;
        }

        var translator = new SqlExpressionTranslator(model, parameters, alias);
        sql += " WHERE " + string.Join(" AND ", queryModel.Predicates.Select(translator.TranslatePredicate));
    }
}
