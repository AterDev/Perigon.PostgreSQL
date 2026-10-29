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

        if (queryModel.Take is not null || queryModel.Skip is not null)
        {
            AppendOrderAndLimit(model, queryModel, parameters, alias, ref sql);
            sql = $"SELECT 1 FROM ({sql}) AS q LIMIT 1";
        }
        else
        {
            sql += " LIMIT 1";
        }

        return new BoundSql(sql, parameters.Parameters);
    }

    private static BoundSql BuildScalar(EntityModel model, QueryModel queryModel, string scalar)
    {
        var parameters = new ParameterBag();
        const string alias = "e";
        if (queryModel.Take is not null || queryModel.Skip is not null)
        {
            var pagedSql = $"SELECT 1 FROM {model.StoreObjectName} AS {alias}";
            AppendWhere(model, queryModel, parameters, alias, ref pagedSql);
            AppendOrderAndLimit(model, queryModel, parameters, alias, ref pagedSql);
            var pagedCommandText = $"SELECT {scalar} FROM ({pagedSql}) AS q";
            return new BoundSql(pagedCommandText, parameters.Parameters);
        }

        var sql = $"SELECT {scalar} FROM {model.StoreObjectName} AS {alias}";
        AppendWhere(model, queryModel, parameters, alias, ref sql);
        return new BoundSql(sql, parameters.Parameters);
    }

    private static void AppendOrderAndLimit(
        EntityModel model,
        QueryModel queryModel,
        ParameterBag parameters,
        string alias,
        ref string sql)
    {
        if (queryModel.Orderings.Count > 0)
        {
            var translator = new SqlExpressionTranslator(model, parameters, alias);
            sql += " ORDER BY " + string.Join(", ", queryModel.Orderings.Select(ordering =>
                translator.TranslateMember(ordering.KeySelector) + (ordering.Descending ? " DESC" : " ASC")));
        }

        if (queryModel.Take is not null)
        {
            sql += " LIMIT " + parameters.Add(queryModel.Take.Value);
        }

        if (queryModel.Skip is not null)
        {
            sql += " OFFSET " + parameters.Add(queryModel.Skip.Value);
        }
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
