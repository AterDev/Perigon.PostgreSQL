using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Sql;

// This deliberately narrow fast path matches a common indexed entity query.
// Entries contain only SQL and member metadata, never query roots or closures.
internal static class RebuiltQueryTemplateCache
{
    private const int MaximumEntries = 128;
    private static readonly ConditionalWeakTable<EntityModel, ModelCache> Models = new();

    public static bool TryBuild(EntityModel model, Expression expression, out BoundSql sql)
    {
        sql = null!;
        var source = expression;
        int? limit = null;
        if (source is MethodCallExpression take && IsQueryable(take, nameof(Queryable.Take)))
        {
            if (take.Arguments.Count != 2 || take.Arguments[1] is not ConstantExpression { Value: int count }) return false;
            limit = count;
            source = take.Arguments[0];
        }

        LambdaExpression? ordering = null;
        var descending = false;
        if (source is MethodCallExpression order &&
            (IsQueryable(order, nameof(Queryable.OrderBy)) || IsQueryable(order, nameof(Queryable.OrderByDescending))))
        {
            if (order.Arguments.Count != 2 || Unquote(order.Arguments[1]) is not LambdaExpression { Parameters.Count: 1 } lambda) return false;
            ordering = lambda;
            descending = order.Method.Name == nameof(Queryable.OrderByDescending);
            source = order.Arguments[0];
        }

        if (source is not MethodCallExpression where ||
            !IsQueryable(where, nameof(Queryable.Where)) || where.Arguments.Count != 2 ||
            where.Arguments[0] is not ConstantExpression ||
            Unquote(where.Arguments[1]) is not LambdaExpression { Parameters.Count: 1 } predicate)
        {
            return false;
        }

        MemberInfo? booleanColumn = null;
        var body = predicate.Body;
        if (body is BinaryExpression { NodeType: ExpressionType.AndAlso, Method: null } and)
        {
            if (and.Left is not MemberExpression active || active.Expression != predicate.Parameters[0] || active.Type != typeof(bool)) return false;
            booleanColumn = active.Member;
            body = and.Right;
        }

        MemberInfo? sortColumn = null;
        if (ordering is not null)
        {
            if (ordering.Body is not MemberExpression sort || sort.Expression != ordering.Parameters[0]) return false;
            sortColumn = sort.Member;
        }

        if (body is not BinaryExpression comparison || comparison.Method is not null || comparison.IsLifted ||
            comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or
                ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual) ||
            comparison.Left is not MemberExpression column || column.Expression != predicate.Parameters[0] || column.Type != typeof(int) ||
            !TryReadInt(comparison.Right, out var value))
        {
            return false;
        }

        var key = new Shape(booleanColumn, column.Member, sortColumn, comparison.NodeType, descending, limit.HasValue);
        var cache = Models.GetValue(model, static _ => new ModelCache());
        string commandText;
        lock (cache.Sync)
        {
            if (!cache.Templates.TryGetValue(key, out commandText!))
            {
                // Build through the ordinary translator to preserve its SQL shape.
                commandText = SelectSqlBuilder.Build(model, QueryModelFactory.Create(expression)).CommandText;
                if (cache.Templates.Count >= MaximumEntries)
                {
                    cache.Templates.Clear();
                }
                cache.Templates.Add(key, commandText);
            }
        }

        sql = new BoundSql(commandText, limit is int countLimit
            ? [new SqlParameterValue(1, value), new SqlParameterValue(2, countLimit)]
            : [new SqlParameterValue(1, value)]);
        return true;
    }

    private static bool IsQueryable(MethodCallExpression call, string name) =>
        call.Method.DeclaringType == typeof(Queryable) && call.Method.Name == name;

    private static Expression Unquote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } unary ? unary.Operand : expression;

    private static bool TryReadInt(Expression expression, out int value)
    {
        if (expression is ConstantExpression { Value: int constant })
        {
            value = constant;
            return true;
        }

        if (expression is MemberExpression { Member: FieldInfo field, Expression: ConstantExpression closure } &&
            field.FieldType == typeof(int) && field.GetValue(closure.Value) is int captured)
        {
            value = captured;
            return true;
        }

        value = default;
        return false;
    }

    private readonly record struct Shape(MemberInfo? BooleanColumn, MemberInfo ValueColumn, MemberInfo? SortColumn,
        ExpressionType Operator, bool Descending, bool HasLimit);

    private sealed class ModelCache
    {
        public object Sync { get; } = new();
        public Dictionary<Shape, string> Templates { get; } = new();
    }
}
