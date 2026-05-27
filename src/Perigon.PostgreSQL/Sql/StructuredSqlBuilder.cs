using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Sql;

internal static class StructuredSqlBuilder
{
    public static BoundSql Build(IQueryable source)
    {
        return Build(source.Expression);
    }

    public static BoundSql Build(Expression expression)
    {
        if (TryBuildPostGroupByQuery(expression, out var groupedQuery))
        {
            return groupedQuery;
        }

        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            if (call.Method.Name == nameof(Queryable.Distinct))
            {
                return WithDistinct(Build(call.Arguments[0]));
            }

            if (call.Method.Name == nameof(Queryable.Select))
            {
                var selector = UnquoteLambda(call.Arguments[1]);
                if (call.Arguments[0] is MethodCallExpression groupBy &&
                    groupBy.Method.Name == nameof(Queryable.GroupBy))
                {
                    return BuildGroupBy(groupBy, selector);
                }

                return BuildProjection(call.Arguments[0], selector);
            }

            if (call.Method.Name == nameof(Queryable.Join))
            {
                return BuildJoin(call);
            }

            if (call.Method.Name == nameof(Queryable.SelectMany) &&
                call.Arguments[0] is MethodCallExpression groupJoin &&
                groupJoin.Method.Name == nameof(Queryable.GroupJoin))
            {
                return BuildLeftJoin(groupJoin, UnquoteLambda(call.Arguments[2]));
            }
        }

        return SelectSqlBuilder.Build(ResolveEntityModel(expression), QueryModelFactory.Create(expression));
    }

    private static BoundSql WithDistinct(BoundSql sql)
    {
        const string select = "SELECT ";
        const string selectDistinct = "SELECT DISTINCT ";
        return sql.CommandText.StartsWith(selectDistinct, StringComparison.Ordinal)
            ? sql
            : sql.CommandText.StartsWith(select, StringComparison.Ordinal)
                ? new BoundSql(selectDistinct + sql.CommandText[select.Length..], sql.Parameters)
                : throw new UnsupportedQueryExpressionException("Distinct can only be applied to SELECT queries.");
    }

    private static bool TryBuildPostGroupByQuery(Expression expression, [NotNullWhen(true)] out BoundSql? sql)
    {
        var query = new PostProjectionQuery();
        if (!TryReadPostProjectionQuery(expression, query, out var selectCall) || !query.HasPostOperation)
        {
            sql = null;
            return false;
        }

        if (selectCall.Arguments[0] is not MethodCallExpression groupBy ||
            groupBy.Method.DeclaringType != typeof(Queryable) ||
            groupBy.Method.Name != nameof(Queryable.GroupBy))
        {
            sql = null;
            return false;
        }

        var selector = UnquoteLambda(selectCall.Arguments[1]);
        if (TryBuildGroupByHavingQuery(groupBy, selector, query, out sql))
        {
            return true;
        }

        var inner = BuildGroupBy(groupBy, selector);
        var parameters = new ParameterBag(inner.Parameters);
        var translator = new ProjectedSqlExpressionTranslator(parameters, "g");
        var commandText = $"SELECT * FROM ({inner.CommandText}) AS g";

        if (query.Predicates.Count > 0)
        {
            commandText += " WHERE " + string.Join(" AND ", query.Predicates.Select(translator.TranslatePredicate));
        }

        if (query.Orderings.Count > 0)
        {
            var orderings = query.Orderings.Select(o =>
                translator.TranslateMember(o.KeySelector) + (o.Descending ? " DESC" : " ASC"));
            commandText += " ORDER BY " + string.Join(", ", orderings);
        }

        if (query.Take is not null)
        {
            commandText += " LIMIT " + parameters.Add(query.Take.Value);
        }

        if (query.Skip is not null)
        {
            commandText += " OFFSET " + parameters.Add(query.Skip.Value);
        }

        sql = new BoundSql(commandText, parameters.Parameters);
        return true;
    }

    private static bool TryBuildGroupByHavingQuery(
        MethodCallExpression groupBy,
        LambdaExpression selector,
        PostProjectionQuery query,
        [NotNullWhen(true)] out BoundSql? sql)
    {
        if (query.Predicates.Count == 0 || query.Orderings.Count > 0 || query.Take is not null || query.Skip is not null)
        {
            sql = null;
            return false;
        }

        var groupQuery = BuildGroupByQuery(groupBy, selector);
        var translator = new ProjectedSqlExpressionTranslator(groupQuery.Parameters, projectionSql: groupQuery.ProjectionSql);
        var commandText = groupQuery.CommandText + " HAVING " + string.Join(" AND ", query.Predicates.Select(translator.TranslatePredicate));
        sql = new BoundSql(commandText, groupQuery.Parameters.Parameters);
        return true;
    }

    private static bool TryReadPostProjectionQuery(
        Expression expression,
        PostProjectionQuery query,
        [NotNullWhen(true)] out MethodCallExpression? selectCall)
    {
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            if (call.Method.Name == nameof(Queryable.Select))
            {
                selectCall = call;
                return true;
            }

            if (IsPostProjectionOperator(call.Method.Name) &&
                TryReadPostProjectionQuery(call.Arguments[0], query, out selectCall))
            {
                switch (call.Method.Name)
                {
                    case nameof(Queryable.Where):
                        query.Predicates.Add(UnquoteLambda(call.Arguments[1]));
                        break;
                    case nameof(Queryable.OrderBy):
                        query.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), false));
                        break;
                    case nameof(Queryable.OrderByDescending):
                        query.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), true));
                        break;
                    case nameof(Queryable.ThenBy):
                        query.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), false));
                        break;
                    case nameof(Queryable.ThenByDescending):
                        query.Orderings.Add(new OrderingModel(UnquoteLambda(call.Arguments[1]), true));
                        break;
                    case nameof(Queryable.Skip):
                        query.Skip = ReadInt(call.Arguments[1]);
                        break;
                    case nameof(Queryable.Take):
                        query.Take = ReadInt(call.Arguments[1]);
                        break;
                }

                return true;
            }
        }

        selectCall = null;
        return false;
    }

    private static bool IsPostProjectionOperator(string methodName)
    {
        return methodName is nameof(Queryable.Where)
            or nameof(Queryable.OrderBy)
            or nameof(Queryable.OrderByDescending)
            or nameof(Queryable.ThenBy)
            or nameof(Queryable.ThenByDescending)
            or nameof(Queryable.Skip)
            or nameof(Queryable.Take);
    }

    private static BoundSql BuildProjection(Expression sourceExpression, LambdaExpression selector)
    {
        var model = ResolveEntityModel(sourceExpression);
        var parameters = new ParameterBag();
        const string alias = "e";
        var selections = ReadSelections(selector.Body, new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>
        {
            [selector.Parameters[0]] = (model, alias)
        });

        var sql = $"SELECT {string.Join(", ", selections)} FROM {model.StoreObjectName} AS {alias}";
        var queryModel = QueryModelFactory.Create(sourceExpression);
        AppendWhereAndOrder(model, queryModel, parameters, alias, ref sql);
        AppendLimitOffset(queryModel, parameters, ref sql);
        return new BoundSql(sql, parameters.Parameters);
    }

    private static BoundSql BuildJoin(MethodCallExpression join)
    {
        var outerExpression = join.Arguments[0];
        var innerExpression = join.Arguments[1];
        var outerModel = ResolveEntityModel(outerExpression);
        var innerModel = ResolveEntityModel(innerExpression);
        var outerKey = UnquoteLambda(join.Arguments[2]);
        var innerKey = UnquoteLambda(join.Arguments[3]);
        var resultSelector = UnquoteLambda(join.Arguments[4]);
        var parameters = new ParameterBag();

        const string outerAlias = "o";
        const string innerAlias = "i";
        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>
        {
            [outerKey.Parameters[0]] = (outerModel, outerAlias),
            [innerKey.Parameters[0]] = (innerModel, innerAlias),
            [resultSelector.Parameters[0]] = (outerModel, outerAlias),
            [resultSelector.Parameters[1]] = (innerModel, innerAlias)
        };

        var selections = ReadSelections(resultSelector.Body, aliases);
        var onLeft = TranslateMember(outerKey.Body, aliases);
        var onRight = TranslateMember(innerKey.Body, aliases);
        var sql = $"SELECT {string.Join(", ", selections)} FROM {outerModel.StoreObjectName} AS {outerAlias} INNER JOIN {innerModel.StoreObjectName} AS {innerAlias} ON {onLeft} = {onRight}";

        var outerQuery = QueryModelFactory.Create(outerExpression);
        var innerQuery = QueryModelFactory.Create(innerExpression);
        var predicates = new List<string>();
        if (outerQuery.Predicates.Count > 0)
        {
            var translator = new SqlExpressionTranslator(outerModel, parameters, outerAlias);
            predicates.AddRange(outerQuery.Predicates.Select(translator.TranslatePredicate));
        }

        if (innerQuery.Predicates.Count > 0)
        {
            var translator = new SqlExpressionTranslator(innerModel, parameters, innerAlias);
            predicates.AddRange(innerQuery.Predicates.Select(translator.TranslatePredicate));
        }

        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        return new BoundSql(sql, parameters.Parameters);
    }

    private static BoundSql BuildLeftJoin(MethodCallExpression groupJoin, LambdaExpression resultSelector)
    {
        var outerExpression = groupJoin.Arguments[0];
        var innerExpression = groupJoin.Arguments[1];
        var outerModel = ResolveEntityModel(outerExpression);
        var innerModel = ResolveEntityModel(innerExpression);
        var outerKey = UnquoteLambda(groupJoin.Arguments[2]);
        var innerKey = UnquoteLambda(groupJoin.Arguments[3]);
        var groupResultSelector = UnquoteLambda(groupJoin.Arguments[4]);
        var parameters = new ParameterBag();

        const string outerAlias = "o";
        const string innerAlias = "i";
        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>
        {
            [outerKey.Parameters[0]] = (outerModel, outerAlias),
            [innerKey.Parameters[0]] = (innerModel, innerAlias),
            [resultSelector.Parameters[1]] = (innerModel, innerAlias)
        };
        var transparentAliases = ReadTransparentAliases(groupResultSelector, resultSelector.Parameters[0], outerModel, outerAlias);

        var selections = ReadSelections(resultSelector.Body, aliases, transparentAliases);
        var onLeft = TranslateMember(outerKey.Body, aliases);
        var onRight = TranslateMember(innerKey.Body, aliases);
        var innerQuery = QueryModelFactory.Create(innerExpression);
        var joinConditions = new List<string> { $"{onLeft} = {onRight}" };
        if (innerQuery.Predicates.Count > 0)
        {
            var translator = new SqlExpressionTranslator(innerModel, parameters, innerAlias);
            joinConditions.AddRange(innerQuery.Predicates.Select(translator.TranslatePredicate));
        }

        var sql = $"SELECT {string.Join(", ", selections)} FROM {outerModel.StoreObjectName} AS {outerAlias} LEFT JOIN {innerModel.StoreObjectName} AS {innerAlias} ON {string.Join(" AND ", joinConditions)}";

        var outerQuery = QueryModelFactory.Create(outerExpression);
        var predicates = new List<string>();
        if (outerQuery.Predicates.Count > 0)
        {
            var translator = new SqlExpressionTranslator(outerModel, parameters, outerAlias);
            predicates.AddRange(outerQuery.Predicates.Select(translator.TranslatePredicate));
        }

        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        return new BoundSql(sql, parameters.Parameters);
    }

    private static BoundSql BuildGroupBy(MethodCallExpression groupBy, LambdaExpression selector)
    {
        var result = BuildGroupByQuery(groupBy, selector);
        return new BoundSql(result.CommandText, result.Parameters.Parameters);
    }

    private static GroupByBuildResult BuildGroupByQuery(MethodCallExpression groupBy, LambdaExpression selector)
    {
        var sourceExpression = groupBy.Arguments[0];
        var model = ResolveEntityModel(sourceExpression);
        var keySelector = UnquoteLambda(groupBy.Arguments[1]);
        var parameters = new ParameterBag();
        const string alias = "e";
        var keySql = ReadGroupKeySql(keySelector.Body, keySelector.Parameters[0], model, alias);
        var selections = ReadGroupSelections(selector.Body, selector.Parameters[0], keySql, model, alias);
        var sql = $"SELECT {string.Join(", ", selections.Select(s => $"{s.Sql} AS {Identifier.Quote(s.OutputName)}"))} FROM {model.StoreObjectName} AS {alias}";
        var queryModel = QueryModelFactory.Create(sourceExpression);
        AppendWhereAndOrder(model, queryModel, parameters, alias, ref sql, appendOrder: false);
        sql += $" GROUP BY {string.Join(", ", keySql.Columns)}";
        return new GroupByBuildResult(
            sql,
            parameters,
            selections.ToDictionary(s => s.OutputName, s => s.Sql, StringComparer.Ordinal));
    }

    private static void AppendWhereAndOrder(
        EntityModel model,
        QueryModel queryModel,
        ParameterBag parameters,
        string alias,
        ref string sql,
        bool appendOrder = true)
    {
        var translator = new SqlExpressionTranslator(model, parameters, alias);
        if (queryModel.Predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", queryModel.Predicates.Select(translator.TranslatePredicate));
        }

        if (appendOrder && queryModel.Orderings.Count > 0)
        {
            var orderings = queryModel.Orderings.Select(o =>
                translator.TranslateMember(o.KeySelector) + (o.Descending ? " DESC" : " ASC"));
            sql += " ORDER BY " + string.Join(", ", orderings);
        }
    }

    private static void AppendLimitOffset(QueryModel queryModel, ParameterBag parameters, ref string sql)
    {
        if (queryModel.Take is not null)
        {
            sql += " LIMIT " + parameters.Add(queryModel.Take.Value);
        }

        if (queryModel.Skip is not null)
        {
            sql += " OFFSET " + parameters.Add(queryModel.Skip.Value);
        }
    }

    private static IReadOnlyList<string> ReadSelections(
        Expression body,
        IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)> aliases)
    {
        return ReadSelections(
            body,
            aliases,
            new Dictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)>());
    }

    private static IReadOnlyList<string> ReadSelections(
        Expression body,
        IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)> aliases,
        IReadOnlyDictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)> transparentAliases)
    {
        return body switch
        {
            NewExpression @new => @new.Arguments.Select((a, i) =>
                Selection(a, @new.Members?[i].Name ?? "value", aliases, transparentAliases)).ToArray(),
            MemberInitExpression init => init.Bindings.OfType<MemberAssignment>()
                .Select(b => Selection(b.Expression, b.Member.Name, aliases, transparentAliases)).ToArray(),
            MemberExpression member => [Selection(member, member.Member.Name, aliases, transparentAliases)],
            _ => throw new UnsupportedQueryExpressionException($"Projection expression '{body}' is not supported.")
        };
    }

    private static string Selection(
        Expression expression,
        string outputName,
        IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)> aliases,
        IReadOnlyDictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)>? transparentAliases = null)
    {
        return $"{TranslateMember(expression, aliases, transparentAliases)} AS {Identifier.Quote(outputName)}";
    }

    private static string TranslateMember(
        Expression expression,
        IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)> aliases,
        IReadOnlyDictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)>? transparentAliases = null)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && member.Expression is ParameterExpression parameter &&
            aliases.TryGetValue(parameter, out var target))
        {
            var column = target.Model.GetColumn(member.Member.Name);
            return $"{target.Alias}.{Identifier.Quote(column.ColumnName)}";
        }

        if (expression is MemberExpression nested &&
            nested.Expression is MemberExpression transparentMember &&
            transparentMember.Expression is ParameterExpression transparentParameter &&
            transparentAliases is not null &&
            transparentAliases.TryGetValue((transparentParameter, transparentMember.Member.Name), out var transparentTarget))
        {
            var column = transparentTarget.Model.GetColumn(nested.Member.Name);
            return $"{transparentTarget.Alias}.{Identifier.Quote(column.ColumnName)}";
        }

        throw new UnsupportedQueryExpressionException($"Only direct mapped member access is supported here. Expression: '{expression}'.");
    }

    private static IReadOnlyDictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)> ReadTransparentAliases(
        LambdaExpression groupResultSelector,
        ParameterExpression transparentParameter,
        EntityModel outerModel,
        string outerAlias)
    {
        var result = new Dictionary<(ParameterExpression Parameter, string MemberName), (EntityModel Model, string Alias)>();
        if (groupResultSelector.Body is not NewExpression @new || @new.Members is null)
        {
            return result;
        }

        for (var i = 0; i < @new.Arguments.Count; i++)
        {
            if (@new.Arguments[i] == groupResultSelector.Parameters[0])
            {
                result[(transparentParameter, @new.Members[i].Name)] = (outerModel, outerAlias);
            }
        }

        return result;
    }

    private static string TranslateSingleMember(Expression expression, ParameterExpression parameter, EntityModel model, string alias)
    {
        return TranslateMember(expression, new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>
        {
            [parameter] = (model, alias)
        });
    }

    private static GroupKeySql ReadGroupKeySql(Expression expression, ParameterExpression parameter, EntityModel model, string alias)
    {
        expression = StripConvert(expression);
        if (expression is NewExpression @new)
        {
            var columns = new List<string>(@new.Arguments.Count);
            var namedColumns = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < @new.Arguments.Count; i++)
            {
                var memberName = @new.Members?[i].Name
                    ?? throw new UnsupportedQueryExpressionException("Multi-key GroupBy requires named key members.");
                var memberSql = TranslateSingleMember(@new.Arguments[i], parameter, model, alias);
                columns.Add(memberSql);
                namedColumns[memberName] = memberSql;
            }

            return new GroupKeySql(columns, namedColumns);
        }

        var keySql = TranslateSingleMember(expression, parameter, model, alias);
        return new GroupKeySql([keySql], new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static IReadOnlyList<GroupProjection> ReadGroupSelections(
        Expression body,
        ParameterExpression groupParameter,
        GroupKeySql keySql,
        EntityModel model,
        string alias)
    {
        if (body is MemberInitExpression init)
        {
            return init.Bindings
                .OfType<MemberAssignment>()
                .Select(b => GroupSelection(b.Expression, b.Member.Name, groupParameter, keySql, model, alias))
                .ToArray();
        }

        if (body is not NewExpression @new)
        {
            throw new UnsupportedQueryExpressionException("GroupBy projection must use a new expression.");
        }

        return @new.Arguments
            .Select((argument, i) => GroupSelection(
                argument,
                @new.Members?[i].Name ?? "value",
                groupParameter,
                keySql,
                model,
                alias))
            .ToArray();
    }

    private static GroupProjection GroupSelection(
        Expression argument,
        string outputName,
        ParameterExpression groupParameter,
        GroupKeySql keySql,
        EntityModel model,
        string alias)
    {
        argument = StripConvert(argument);
        if (argument is MemberExpression { Member.Name: "Key", Expression: var target } &&
            target == groupParameter)
        {
            if (keySql.Columns.Count != 1)
            {
                throw new UnsupportedQueryExpressionException("Project individual key members for multi-key GroupBy.");
            }

            return new GroupProjection(outputName, keySql.Columns[0]);
        }

        if (argument is MemberExpression keyMember &&
            keyMember.Expression is MemberExpression { Member.Name: "Key", Expression: var keyTarget } &&
            keyTarget == groupParameter &&
            keySql.NamedColumns.TryGetValue(keyMember.Member.Name, out var keyColumn))
        {
            return new GroupProjection(outputName, keyColumn);
        }

        if (argument is MethodCallExpression aggregate)
        {
            return new GroupProjection(outputName, TranslateAggregate(aggregate, model, alias));
        }

        throw new UnsupportedQueryExpressionException($"GroupBy projection '{argument}' is not supported.");
    }

    private static string TranslateAggregate(MethodCallExpression aggregate, EntityModel model, string alias)
    {
        if ((aggregate.Method.Name is nameof(PostgresAggregateExtensions.CountDistinct)
            or nameof(PostgresAggregateExtensions.LongCountDistinct)) &&
            aggregate.Arguments.Count == 2)
        {
            var lambda = UnquoteLambda(aggregate.Arguments[1]);
            var memberSql = TranslateSingleMember(lambda.Body, lambda.Parameters[0], model, alias);
            return $"count(distinct {memberSql})";
        }

        if (aggregate.Method.Name == nameof(PostgresAggregateExtensions.ArrayAgg) && aggregate.Arguments.Count == 2)
        {
            var lambda = UnquoteLambda(aggregate.Arguments[1]);
            var memberSql = TranslateSingleMember(lambda.Body, lambda.Parameters[0], model, alias);
            return $"array_agg({memberSql})";
        }

        if (aggregate.Method.Name == nameof(PostgresAggregateExtensions.JsonbAgg) && aggregate.Arguments.Count == 2)
        {
            var lambda = UnquoteLambda(aggregate.Arguments[1]);
            var memberSql = TranslateSingleMember(lambda.Body, lambda.Parameters[0], model, alias);
            return $"jsonb_agg({memberSql})::text";
        }

        var functionName = aggregate.Method.Name switch
        {
            nameof(Enumerable.Count) => "count",
            nameof(Enumerable.LongCount) => "count",
            nameof(Enumerable.Sum) => "sum",
            nameof(Enumerable.Min) => "min",
            nameof(Enumerable.Max) => "max",
            nameof(Enumerable.Average) => "avg",
            _ => throw new UnsupportedQueryExpressionException($"Aggregate '{aggregate.Method.Name}' is not supported.")
        };

        if (functionName == "count" && aggregate.Arguments.Count == 1)
        {
            return "count(*)";
        }

        if (aggregate.Arguments.Count == 2)
        {
            var lambda = UnquoteLambda(aggregate.Arguments[1]);
            var memberSql = TranslateSingleMember(lambda.Body, lambda.Parameters[0], model, alias);
            return $"{functionName}({memberSql})";
        }

        throw new UnsupportedQueryExpressionException($"Aggregate '{aggregate.Method.Name}' shape is not supported.");
    }

    private static int ReadInt(Expression expression)
    {
        if (expression is ConstantExpression { Value: int value })
        {
            return value;
        }

        var evaluated = ExpressionValueReader.Read(expression);
        return evaluated is int valueFromExpression
            ? valueFromExpression
            : throw new UnsupportedQueryExpressionException("Skip/Take require integer values.");
    }

    private static string Operator(ExpressionType nodeType)
    {
        return nodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            _ => throw new UnsupportedQueryExpressionException($"Binary operator '{nodeType}' is not supported.")
        };
    }

    private sealed class PostProjectionQuery
    {
        public List<LambdaExpression> Predicates { get; } = [];

        public List<OrderingModel> Orderings { get; } = [];

        public int? Skip { get; set; }

        public int? Take { get; set; }

        public bool HasPostOperation => Predicates.Count > 0 || Orderings.Count > 0 || Skip is not null || Take is not null;
    }

    private sealed record GroupKeySql(
        IReadOnlyList<string> Columns,
        IReadOnlyDictionary<string, string> NamedColumns);

    private sealed record GroupProjection(string OutputName, string Sql);

    private sealed record GroupByBuildResult(
        string CommandText,
        ParameterBag Parameters,
        IReadOnlyDictionary<string, string> ProjectionSql);

    private sealed class ProjectedSqlExpressionTranslator : ExpressionVisitor
    {
        private readonly ParameterBag _parameters;
        private readonly string _alias;
        private readonly IReadOnlyDictionary<string, string>? _projectionSql;
        private readonly Stack<string> _sql = new();
        private ParameterExpression? _projectionParameter;

        public ProjectedSqlExpressionTranslator(
            ParameterBag parameters,
            string alias = "g",
            IReadOnlyDictionary<string, string>? projectionSql = null)
        {
            _parameters = parameters;
            _alias = alias;
            _projectionSql = projectionSql;
        }

        public string TranslatePredicate(LambdaExpression expression)
        {
            _projectionParameter = expression.Parameters[0];
            Visit(expression.Body);
            return _sql.Pop();
        }

        public string TranslateMember(LambdaExpression expression)
        {
            _projectionParameter = expression.Parameters[0];
            Visit(expression.Body);
            return _sql.Pop();
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            {
                Visit(node.Left);
                var left = _sql.Pop();
                Visit(node.Right);
                var right = _sql.Pop();
                _sql.Push($"({left} {Operator(node.NodeType)} {right})");
                return node;
            }

            if (IsNullConstant(node.Right) || TryReadNull(node.Right))
            {
                Visit(node.Left);
                var left = _sql.Pop();
                _sql.Push(node.NodeType == ExpressionType.Equal ? $"{left} IS NULL" : $"{left} IS NOT NULL");
                return node;
            }

            if (IsNullConstant(node.Left) || TryReadNull(node.Left))
            {
                Visit(node.Right);
                var right = _sql.Pop();
                _sql.Push(node.NodeType == ExpressionType.Equal ? $"{right} IS NULL" : $"{right} IS NOT NULL");
                return node;
            }

            Visit(node.Left);
            var sqlLeft = _sql.Pop();
            Visit(node.Right);
            var sqlRight = _sql.Pop();
            _sql.Push($"({sqlLeft} {Operator(node.NodeType)} {sqlRight})");
            return node;
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Not)
            {
                Visit(node.Operand);
                _sql.Push($"NOT ({_sql.Pop()})");
                return node;
            }

            if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                Visit(node.Operand);
                return node;
            }

            return base.VisitUnary(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _projectionParameter)
            {
                if (_projectionSql is not null && _projectionSql.TryGetValue(node.Member.Name, out var projectionSql))
                {
                    _sql.Push(projectionSql);
                }
                else
                {
                    _sql.Push($"{_alias}.{Identifier.Quote(node.Member.Name)}");
                }

                return node;
            }

            if (node.Member.Name == "Length" && node.Expression is not null)
            {
                Visit(node.Expression);
                _sql.Push($"length({_sql.Pop()})");
                return node;
            }

            var value = ExpressionValueReader.Read(node);
            _sql.Push(_parameters.Add(value));
            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            _sql.Push(_parameters.Add(node.Value));
            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(string) &&
                node.Method.Name == nameof(string.IsNullOrEmpty) &&
                node.Arguments.Count == 1)
            {
                Visit(node.Arguments[0]);
                var value = _sql.Pop();
                _sql.Push($"({value} IS NULL OR {value} = '')");
                return node;
            }

            if (node.Method.DeclaringType == typeof(string) &&
                node.Method.Name == nameof(string.IsNullOrWhiteSpace) &&
                node.Arguments.Count == 1)
            {
                Visit(node.Arguments[0]);
                var value = _sql.Pop();
                _sql.Push($"({value} IS NULL OR btrim({value}) = '')");
                return node;
            }

            if (node.Method.Name == nameof(string.Contains) && node.Object is not null && node.Object.Type == typeof(string))
            {
                Visit(node.Object);
                var instance = _sql.Pop();
                var value = ExpressionValueReader.Read(node.Arguments[0]);
                _sql.Push($"{instance} LIKE {_parameters.Add("%" + value + "%")}");
                return node;
            }

            if (node.Method.Name == nameof(string.StartsWith) && node.Object is not null)
            {
                Visit(node.Object);
                var instance = _sql.Pop();
                var value = ExpressionValueReader.Read(node.Arguments[0]);
                _sql.Push($"{instance} LIKE {_parameters.Add(value + "%")}");
                return node;
            }

            if (node.Method.Name == nameof(string.Trim) && node.Object is not null && node.Arguments.Count == 0)
            {
                Visit(node.Object);
                _sql.Push($"btrim({_sql.Pop()})");
                return node;
            }

            if (node.Method.Name == nameof(string.TrimStart) && node.Object is not null && node.Arguments.Count == 0)
            {
                Visit(node.Object);
                _sql.Push($"ltrim({_sql.Pop()})");
                return node;
            }

            if (node.Method.Name == nameof(string.TrimEnd) && node.Object is not null && node.Arguments.Count == 0)
            {
                Visit(node.Object);
                _sql.Push($"rtrim({_sql.Pop()})");
                return node;
            }

            if (node.Method.Name == nameof(string.EndsWith) && node.Object is not null)
            {
                Visit(node.Object);
                var instance = _sql.Pop();
                var value = ExpressionValueReader.Read(node.Arguments[0]);
                _sql.Push($"{instance} LIKE {_parameters.Add("%" + value)}");
                return node;
            }

            throw new UnsupportedQueryExpressionException(
                $"Method call '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported after projection.");
        }

        private static bool IsNullConstant(Expression expression)
        {
            expression = StripConvert(expression);
            return expression is ConstantExpression { Value: null };
        }

        private static bool TryReadNull(Expression expression)
        {
            if (expression is MemberExpression { Expression: ParameterExpression })
            {
                return false;
            }

            try
            {
                return ExpressionValueReader.Read(expression) is null;
            }
            catch (UnsupportedQueryExpressionException)
            {
                return false;
            }
        }
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2073",
        Justification = "This reflection fallback is limited to the current SQL-preview MVP. Production execution will use source-generated entity metadata.")]
    private static Type FindRootEntityType(Expression expression)
    {
        expression = StripQuote(expression);
        if (expression is ConstantExpression { Value: IQueryable queryable })
        {
            return queryable.ElementType;
        }

        if (expression is MethodCallExpression call && call.Arguments.Count > 0)
        {
            return FindRootEntityType(call.Arguments[0]);
        }

        throw new UnsupportedQueryExpressionException($"Cannot find query root for expression '{expression}'.");
    }

    private static EntityModel ResolveEntityModel(Expression expression)
    {
        expression = StripQuote(expression);
        if (expression is ConstantExpression { Value: IEntityModelSource source })
        {
            return source.Model;
        }

        if (expression is MethodCallExpression call && call.Arguments.Count > 0)
        {
            return ResolveEntityModel(call.Arguments[0]);
        }

        return EntityModel.For(FindRootEntityType(expression));
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        expression = StripQuote(expression);
        return expression as LambdaExpression
            ?? throw new UnsupportedQueryExpressionException($"Expression '{expression}' is not a lambda.");
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
