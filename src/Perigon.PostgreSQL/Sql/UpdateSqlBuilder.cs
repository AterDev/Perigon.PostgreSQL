using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Update;

namespace Perigon.PostgreSQL.Sql;

internal static class UpdateSqlBuilder
{
    public static BoundSql Build<T>(
        EntityModel model,
        QueryModel queryModel,
        Expression<Func<UpdateSetters<T>, UpdateSetters<T>>> setters,
        bool allowFullTableUpdate)
        where T : class
    {
        if (queryModel.Predicates.Count == 0 && !allowFullTableUpdate)
        {
            throw new InvalidOperationException(
                "Refusing to execute UPDATE without WHERE. Pass options that explicitly allow full-table update.");
        }

        var parameters = new ParameterBag();
        const string alias = "e";
        var explicitlySet = new HashSet<string>(StringComparer.Ordinal);
        var assignments = ReadAssignments<T>(model, setters.Body, parameters, explicitlySet);
        if (assignments.Count == 0)
        {
            throw new InvalidOperationException("UPDATE requires at least one Set call.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var column in model.Columns.Where(c => c.TimestampOnUpdate && !explicitlySet.Contains(c.PropertyName)))
        {
            assignments.Add($"{Identifier.Quote(column.ColumnName)} = {parameters.Add(ValueGeneration.Timestamp(column, now))}");
        }

        var sql = $"UPDATE {model.StoreObjectName} AS {alias} SET " + string.Join(", ", assignments);
        var translator = new SqlExpressionTranslator(model, parameters, alias);
        if (queryModel.Predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", queryModel.Predicates.Select(translator.TranslatePredicate));
        }

        return new BoundSql(sql, parameters.Parameters);
    }

    private static List<string> ReadAssignments<T>(
        EntityModel model,
        Expression expression,
        ParameterBag parameters,
        HashSet<string> explicitlySet)
        where T : class
    {
        var assignments = new List<string>();
        ReadAssignmentsRecursive(model, expression, parameters, assignments, explicitlySet);
        return assignments;
    }

    private static void ReadAssignmentsRecursive(
        EntityModel model,
        Expression expression,
        ParameterBag parameters,
        List<string> assignments,
        HashSet<string> explicitlySet)
    {
        if (expression is MethodCallExpression call &&
            call.Method.Name is nameof(UpdateSetters<object>.Set) or nameof(UpdateSetters<object>.SetExpression))
        {
            ReadAssignmentsRecursive(model, call.Object ?? call.Arguments[0], parameters, assignments, explicitlySet);
            var propertyLambda = UnquoteLambda(call.Arguments[^2]);
            var member = StripConvert(propertyLambda.Body) as MemberExpression
                ?? throw new UnsupportedQueryExpressionException("Update Set target must be a mapped member.");
            var column = model.GetColumn(member.Member.Name);
            explicitlySet.Add(column.PropertyName);
            var valueSql = ReadUpdateValueSql(model, parameters, call.Arguments[^1]);
            assignments.Add($"{Identifier.Quote(column.ColumnName)} = {valueSql}");
        }
    }

    private static string ReadUpdateValueSql(
        EntityModel model,
        ParameterBag parameters,
        Expression valueExpression)
    {
        if (StripQuote(valueExpression) is LambdaExpression lambda)
        {
            var translator = new SqlExpressionTranslator(model, parameters, "e");
            return translator.TranslateMember(lambda);
        }

        var value = ExpressionValueReader.Read(valueExpression);
        return parameters.Add(value);
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        return (LambdaExpression)StripQuote(expression);
    }

    private static Expression StripQuote(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        return expression;
    }
}
