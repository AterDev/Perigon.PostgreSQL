using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Expressions;

internal sealed class SqlExpressionTranslator : ExpressionVisitor
{
    private readonly EntityModel _model;
    private readonly ParameterBag _parameters;
    private readonly string _alias;
    private readonly IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)>? _correlatedAliases;
    private readonly bool _useCSharpNullSemantics;
    private readonly bool _allowNonNullableAggregateProjection;
    private readonly Stack<string> _sql = new();
    private ParameterExpression? _entityParameter;

    public SqlExpressionTranslator(
        EntityModel model,
        ParameterBag parameters,
        string alias,
        IReadOnlyDictionary<ParameterExpression, (EntityModel Model, string Alias)>? correlatedAliases = null,
        bool useCSharpNullSemantics = false,
        bool allowNonNullableAggregateProjection = false)
    {
        _model = model;
        _parameters = parameters;
        _alias = alias;
        _correlatedAliases = correlatedAliases;
        _useCSharpNullSemantics = useCSharpNullSemantics;
        _allowNonNullableAggregateProjection = allowNonNullableAggregateProjection;
    }

    public string TranslatePredicate(LambdaExpression expression)
    {
        _entityParameter = expression.Parameters[0];
        Visit(expression.Body);
        return _sql.Pop();
    }

    public string TranslateMember(LambdaExpression expression)
    {
        _entityParameter = expression.Parameters[0];
        Visit(expression.Body);
        return _sql.Pop();
    }

    public string TranslateScalar(Expression expression)
    {
        Visit(expression);
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

        if ((!_useCSharpNullSemantics || node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual) &&
            (IsNullConstant(node.Right) || TryReadNonEntityNull(node.Right)))
        {
            Visit(node.Left);
            var left = _sql.Pop();
            _sql.Push(node.NodeType == ExpressionType.Equal ? $"{left} IS NULL" : $"{left} IS NOT NULL");
            return node;
        }

        if ((!_useCSharpNullSemantics || node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual) &&
            (IsNullConstant(node.Left) || TryReadNonEntityNull(node.Left)))
        {
            Visit(node.Right);
            var right = _sql.Pop();
            _sql.Push(node.NodeType == ExpressionType.Equal ? $"{right} IS NULL" : $"{right} IS NOT NULL");
            return node;
        }

        if (_useCSharpNullSemantics && node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            Visit(node.Left);
            var left = _sql.Pop();
            Visit(node.Right);
            var right = _sql.Pop();
            _sql.Push($"({left} {(node.NodeType == ExpressionType.Equal ? "IS NOT DISTINCT FROM" : "IS DISTINCT FROM")} {right})");
            return node;
        }

        Visit(node.Left);
        var sqlLeft = _sql.Pop();
        Visit(node.Right);
        var sqlRight = _sql.Pop();
        var comparison = $"({sqlLeft} {Operator(node.NodeType)} {sqlRight})";
        _sql.Push(_useCSharpNullSemantics && node.NodeType is
            ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
            ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            ? $"COALESCE({comparison}, FALSE)"
            : comparison);
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.NodeType == ExpressionType.Not)
        {
            Visit(node.Operand);
            var operand = _sql.Pop();
            _sql.Push(_useCSharpNullSemantics
                ? $"NOT (COALESCE({operand}, FALSE))"
                : $"NOT ({operand})");
            return node;
        }

        if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            Visit(node.Operand);
            return node;
        }

        if (node.NodeType == ExpressionType.ArrayLength)
        {
            Visit(node.Operand);
            _sql.Push($"cardinality({_sql.Pop()})");
            return node;
        }

        return base.VisitUnary(node);
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == _entityParameter)
        {
            var column = _model.GetColumn(node.Member.Name);
            _sql.Push($"{_alias}.{Identifier.Quote(column.ColumnName)}");
            return node;
        }

        if (node.Expression is ParameterExpression correlatedParameter &&
            _correlatedAliases is not null &&
            _correlatedAliases.TryGetValue(correlatedParameter, out var correlated))
        {
            var column = correlated.Model.GetColumn(node.Member.Name);
            _sql.Push($"{correlated.Alias}.{Identifier.Quote(column.ColumnName)}");
            return node;
        }

        if (node.Member.Name == nameof(Nullable<int>.HasValue) && node.Expression is not null)
        {
            Visit(node.Expression);
            _sql.Push($"{_sql.Pop()} IS NOT NULL");
            return node;
        }

        if (node.Member.Name == nameof(Nullable<int>.Value) && node.Expression is not null)
        {
            Visit(node.Expression);
            return node;
        }

        if (node.Member.Name == "Length" && node.Expression is not null && IsEntityMember(node.Expression))
        {
            Visit(node.Expression);
            var lengthTarget = _sql.Pop();
            _sql.Push(node.Expression.Type == typeof(string) ? $"length({lengthTarget})" : $"cardinality({lengthTarget})");
            return node;
        }

        var value = ExpressionValueReader.Read(node);
        _sql.Push(_parameters.Add(value));
        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable)
        {
            return node;
        }

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

        if (StringSqlTranslation.IsExactInstanceMethod(node, nameof(string.Contains), typeof(string)))
        {
            Visit(node.Object);
            var instance = _sql.Pop();
            var pattern = StringSqlTranslation.ReadLikePattern(node.Arguments[0], "%", "%");
            _sql.Push($"{instance} LIKE {_parameters.Add(pattern)} ESCAPE '\\'");
            return node;
        }

        if (StringSqlTranslation.IsExactInstanceMethod(node, nameof(string.StartsWith), typeof(string)))
        {
            Visit(node.Object);
            var instance = _sql.Pop();
            var pattern = StringSqlTranslation.ReadLikePattern(node.Arguments[0], "", "%");
            _sql.Push($"{instance} LIKE {_parameters.Add(pattern)} ESCAPE '\\'");
            return node;
        }

        if (StringSqlTranslation.IsExactInstanceMethod(node, nameof(string.EndsWith), typeof(string)))
        {
            Visit(node.Object);
            var instance = _sql.Pop();
            var pattern = StringSqlTranslation.ReadLikePattern(node.Arguments[0], "%", "");
            _sql.Push($"{instance} LIKE {_parameters.Add(pattern)} ESCAPE '\\'");
            return node;
        }

        if (node.Method.Name == nameof(string.Substring) && node.Object is not null)
        {
            Visit(node.Object);
            var instance = _sql.Pop();
            var start = ReadSubstringStart(node.Arguments[0]);
            if (node.Arguments.Count == 1)
            {
                _sql.Push($"substring({instance} from {_parameters.Add(start)})");
                return node;
            }

            var length = ExpressionValueReader.Read(node.Arguments[1]);
            _sql.Push($"substring({instance} from {_parameters.Add(start)} for {_parameters.Add(length)})");
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

        if (StringSqlTranslation.IsExactInstanceMethod(node, nameof(string.ToLower)))
        {
            Visit(node.Object);
            _sql.Push($"lower({_sql.Pop()})");
            return node;
        }

        if (StringSqlTranslation.IsExactInstanceMethod(node, nameof(string.ToUpper)))
        {
            Visit(node.Object);
            _sql.Push($"upper({_sql.Pop()})");
            return node;
        }

        if ((node.Method.DeclaringType == typeof(Enumerable) ||
             node.Method.DeclaringType == typeof(MemoryExtensions)) &&
            node.Method.Name == nameof(Enumerable.Contains))
        {
            return VisitContains(node);
        }

        if (node.Method.DeclaringType == typeof(Enumerable) &&
            node.Method.Name == nameof(Enumerable.Any))
        {
            return VisitEnumerableAny(node);
        }

        if (node.Method.DeclaringType == typeof(Enumerable) &&
            node.Method.Name == nameof(Enumerable.All))
        {
            return VisitEnumerableAll(node);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.Any))
        {
            return VisitQueryableAny(node);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.LongCount))
        {
            return VisitQueryableCount(node, castToInteger: false);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.Count))
        {
            return VisitQueryableCount(node, castToInteger: true);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.Sum))
        {
            return VisitQueryableSum(node);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name is nameof(Queryable.Min) or nameof(Queryable.Max) or nameof(Queryable.Average))
        {
            return VisitQueryableNullableAggregate(node);
        }

        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.All))
        {
            return VisitQueryableAll(node);
        }

        if (node.Method.Name is "ContainsAll" or "Overlaps" or "IsContainedBy")
        {
            var source = node.Object ?? node.Arguments[0];
            var valueExpression = node.Object is null ? node.Arguments[1] : node.Arguments[0];
            Visit(source);
            var column = _sql.Pop();
            var value = ExpressionValueReader.Read(valueExpression);
            var op = node.Method.Name switch
            {
                "ContainsAll" => "@>",
                "Overlaps" => "&&",
                "IsContainedBy" => "<@",
                _ => throw new UnreachableException()
            };
            _sql.Push($"{column} {op} {_parameters.Add(value)}");
            return node;
        }

        if (node.Method.Name == nameof(JsonbQueryableExtensions.JsonbContains))
        {
            var source = node.Object ?? node.Arguments[0];
            var valueExpression = node.Object is null ? node.Arguments[1] : node.Arguments[0];
            Visit(source);
            var column = _sql.Pop();
            _sql.Push($"{column} @> ({_parameters.Add(ExpressionValueReader.Read(valueExpression))})::jsonb");
            return node;
        }

        if (node.Method.Name == nameof(JsonbQueryableExtensions.JsonbHasKey))
        {
            var source = node.Object ?? node.Arguments[0];
            var valueExpression = node.Object is null ? node.Arguments[1] : node.Arguments[0];
            Visit(source);
            var column = _sql.Pop();
            _sql.Push($"{column} ? {_parameters.Add(ExpressionValueReader.Read(valueExpression))}");
            return node;
        }

        if (node.Method.Name == nameof(JsonbQueryableExtensions.JsonbText))
        {
            var source = node.Object ?? node.Arguments[0];
            var valueExpression = node.Object is null ? node.Arguments[1] : node.Arguments[0];
            Visit(source);
            var column = _sql.Pop();
            _sql.Push($"{column} ->> {_parameters.Add(ExpressionValueReader.Read(valueExpression))}");
            return node;
        }

        if (node.Method.Name == nameof(JsonbQueryableExtensions.JsonbPathExists))
        {
            var source = node.Object ?? node.Arguments[0];
            var valueExpression = node.Object is null ? node.Arguments[1] : node.Arguments[0];
            Visit(source);
            var column = _sql.Pop();
            _sql.Push($"{column} @? ({_parameters.Add(ExpressionValueReader.Read(valueExpression))})::jsonpath");
            return node;
        }

        throw new UnsupportedQueryExpressionException(
            $"Method call '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported in SQL translation.");
    }

    private Expression VisitContains(MethodCallExpression node)
    {
        Expression source;
        Expression item;

        if (node.Object is not null)
        {
            source = node.Object;
            item = node.Arguments[0];
        }
        else
        {
            source = node.Arguments[0];
            item = node.Arguments[1];
        }

        if (IsEntityMember(source))
        {
            Visit(StripConvert(source));
            var column = _sql.Pop();
            var value = ExpressionValueReader.Read(item);
            _sql.Push($"{column} @> ARRAY[{_parameters.Add(value)}]");
            return node;
        }

        var values = ExpressionValueReader.Read(source);
        if (values is not IEnumerable || values is string)
        {
            throw new UnsupportedQueryExpressionException($"Contains source '{source}' must be a collection.");
        }

        Visit(item);
        var sqlItem = _sql.Pop();
        _sql.Push($"{sqlItem} = ANY({_parameters.Add(values)})");
        return node;
    }

    private Expression VisitEnumerableAny(MethodCallExpression node)
    {
        var source = node.Arguments[0];
        if (!IsEntityMember(source))
        {
            throw new UnsupportedQueryExpressionException("Only entity array Any is supported in SQL translation.");
        }

        Visit(StripConvert(source));
        var column = _sql.Pop();
        if (node.Arguments.Count == 1)
        {
            _sql.Push($"cardinality({column}) > 0");
            return node;
        }

        var lambda = UnquoteLambda(node.Arguments[1]);
        if (TryTranslateArrayStringPredicate(lambda, column, out var stringPredicate))
        {
            _sql.Push(stringPredicate);
            return node;
        }

        if (StripConvert(lambda.Body) is not BinaryExpression { NodeType: ExpressionType.Equal } binary)
        {
            throw new UnsupportedQueryExpressionException("Array Any supports equality predicates and string Contains, StartsWith, or EndsWith predicates.");
        }

        var valueExpression = TryReadAnyPredicateValue(binary.Left, binary.Right, lambda.Parameters[0])
            ?? TryReadAnyPredicateValue(binary.Right, binary.Left, lambda.Parameters[0])
            ?? throw new UnsupportedQueryExpressionException("Array Any predicate must compare the element to a value.");
        _sql.Push($"{_parameters.Add(ExpressionValueReader.Read(valueExpression))} = ANY({column})");
        return node;
    }

    private bool TryTranslateArrayStringPredicate(
        LambdaExpression lambda,
        string column,
        out string sql)
    {
        sql = string.Empty;
        if (StripConvert(lambda.Body) is not MethodCallExpression call ||
            call.Object is null ||
            StripConvert(call.Object) != lambda.Parameters[0] ||
            call.Method.DeclaringType != typeof(string) ||
            call.Arguments.Count != 1 ||
            call.Arguments[0].Type != typeof(string))
        {
            return false;
        }

        var (prefix, suffix) = call.Method.Name switch
        {
            nameof(string.Contains) => ("%", "%"),
            nameof(string.StartsWith) => (string.Empty, "%"),
            nameof(string.EndsWith) => ("%", string.Empty),
            _ => (null, null)
        };
        if (prefix is null || suffix is null)
        {
            return false;
        }

        var pattern = StringSqlTranslation.ReadLikePattern(call.Arguments[0], prefix, suffix);
        sql = $"EXISTS (SELECT 1 FROM unnest({column}) AS p(\"value\") WHERE p.\"value\" LIKE {_parameters.Add(pattern)} ESCAPE '\\')";
        return true;
    }

    private Expression VisitEnumerableAll(MethodCallExpression node)
    {
        var source = node.Arguments[0];
        if (!IsEntityMember(source))
        {
            throw new UnsupportedQueryExpressionException("Only entity array All is supported in SQL translation.");
        }

        if (node.Arguments.Count != 2)
        {
            throw new UnsupportedQueryExpressionException("Array All requires a predicate in SQL translation.");
        }

        Visit(StripConvert(source));
        var column = _sql.Pop();
        var lambda = UnquoteLambda(node.Arguments[1]);
        if (StripConvert(lambda.Body) is not BinaryExpression { NodeType: ExpressionType.Equal } binary)
        {
            throw new UnsupportedQueryExpressionException("Array All currently supports equality predicates only.");
        }

        var valueExpression = TryReadAnyPredicateValue(binary.Left, binary.Right, lambda.Parameters[0])
            ?? TryReadAnyPredicateValue(binary.Right, binary.Left, lambda.Parameters[0])
            ?? throw new UnsupportedQueryExpressionException("Array All predicate must compare the element to a value.");
        _sql.Push($"({column} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM unnest({column}) AS p(\"value\") WHERE p.\"value\" IS DISTINCT FROM {_parameters.Add(ExpressionValueReader.Read(valueExpression))}))");
        return node;
    }

    private Expression VisitQueryableAny(MethodCallExpression node)
    {
        if (node.Arguments.Count is < 1 or > 2)
        {
            throw new UnsupportedQueryExpressionException("Queryable Any requires a query source and optional predicate.");
        }

        var source = RewriteCapturedQueryableRoots(node.Arguments[0]);
        source = ResolveQueryableExpression(source);
        var model = ResolveQueryModel(source);
        var query = QueryModelFactory.Create(source);
        if (query.Orderings.Count > 0 || query.Skip is not null || query.Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Queryable Any subqueries do not support ordering or paging.");
        }

        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>();
        if (_entityParameter is not null)
        {
            aliases[_entityParameter] = (_model, _alias);
        }

        if (_correlatedAliases is not null)
        {
            foreach (var pair in _correlatedAliases)
            {
                aliases[pair.Key] = pair.Value;
            }
        }

        var subqueryAlias = _alias == "e" ? "s" : _alias + "s";
        var predicates = new List<string>();
        foreach (var predicate in query.Predicates)
        {
            var innerTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(innerTranslator.TranslatePredicate(predicate));
        }

        if (node.Arguments.Count == 2)
        {
            var predicate = UnquoteLambda(node.Arguments[1]);
            var innerTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(innerTranslator.TranslatePredicate(predicate));
        }

        var sql = $"EXISTS (SELECT 1 FROM {model.StoreObjectName} AS {subqueryAlias}";
        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        _sql.Push(sql + ")");
        return node;
    }

    private Expression VisitQueryableAll(MethodCallExpression node)
    {
        if (node.Arguments.Count != 2)
        {
            throw new UnsupportedQueryExpressionException("Queryable All requires a query source and predicate.");
        }

        var source = RewriteCapturedQueryableRoots(node.Arguments[0]);
        source = ResolveQueryableExpression(source);
        var model = ResolveQueryModel(source);
        var query = QueryModelFactory.Create(source);
        if (query.Orderings.Count > 0 || query.Skip is not null || query.Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Queryable All subqueries do not support ordering or paging.");
        }

        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>();
        if (_entityParameter is not null)
        {
            aliases[_entityParameter] = (_model, _alias);
        }

        if (_correlatedAliases is not null)
        {
            foreach (var pair in _correlatedAliases)
            {
                aliases[pair.Key] = pair.Value;
            }
        }

        var subqueryAlias = _alias == "e" ? "s" : _alias + "s";
        var conditions = new List<string>();
        foreach (var predicate in query.Predicates)
        {
            var innerTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            conditions.Add(innerTranslator.TranslatePredicate(predicate));
        }

        var allPredicate = UnquoteLambda(node.Arguments[1]);
        var predicateTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
        var predicateSql = predicateTranslator.TranslatePredicate(allPredicate);
        // IS NOT TRUE preserves C# All semantics for nullable SQL predicates:
        // both FALSE and NULL violate the universal predicate.
        conditions.Add($"({predicateSql}) IS NOT TRUE");

        var sql = $"NOT EXISTS (SELECT 1 FROM {model.StoreObjectName} AS {subqueryAlias} WHERE {string.Join(" AND ", conditions)})";
        _sql.Push(sql);
        return node;
    }

    private Expression VisitQueryableCount(MethodCallExpression node, bool castToInteger)
    {
        if (node.Arguments.Count is < 1 or > 2)
        {
            throw new UnsupportedQueryExpressionException("Queryable LongCount requires a query source and optional predicate.");
        }

        var source = RewriteCapturedQueryableRoots(node.Arguments[0]);
        source = ResolveQueryableExpression(source);
        var model = ResolveQueryModel(source);
        var query = QueryModelFactory.Create(source);
        if (query.Orderings.Count > 0 || query.Skip is not null || query.Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Queryable LongCount subqueries do not support ordering or paging.");
        }

        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>();
        if (_entityParameter is not null)
        {
            aliases[_entityParameter] = (_model, _alias);
        }

        if (_correlatedAliases is not null)
        {
            foreach (var pair in _correlatedAliases)
            {
                aliases[pair.Key] = pair.Value;
            }
        }

        var subqueryAlias = _alias == "e" ? "s" : _alias + "s";
        var predicates = new List<string>();
        foreach (var predicate in query.Predicates)
        {
            var translator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(translator.TranslatePredicate(predicate));
        }

        if (node.Arguments.Count == 2)
        {
            var predicate = UnquoteLambda(node.Arguments[1]);
            var translator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(translator.TranslatePredicate(predicate));
        }

        var countSql = castToInteger ? "count(*)::integer" : "count(*)";
        var sql = $"(SELECT {countSql} FROM {model.StoreObjectName} AS {subqueryAlias}";
        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        _sql.Push(sql + ")");
        return node;
    }

    private Expression VisitQueryableSum(MethodCallExpression node)
    {
        if (node.Arguments.Count != 2)
        {
            throw new UnsupportedQueryExpressionException(
                "Queryable Sum subqueries require a source and a scalar selector.");
        }

        var source = RewriteCapturedQueryableRoots(node.Arguments[0]);
        source = ResolveQueryableExpression(source);
        var model = ResolveQueryModel(source);
        var query = QueryModelFactory.Create(source);
        if (query.Orderings.Count > 0 || query.Skip is not null || query.Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Queryable Sum subqueries do not support ordering or paging.");
        }

        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>();
        if (_entityParameter is not null)
        {
            aliases[_entityParameter] = (_model, _alias);
        }

        if (_correlatedAliases is not null)
        {
            foreach (var pair in _correlatedAliases)
            {
                aliases[pair.Key] = pair.Value;
            }
        }

        var subqueryAlias = _alias == "e" ? "s" : _alias + "s";
        var selector = UnquoteLambda(node.Arguments[1]);
        EnsureNoNullableValueDereference(node.Method.Name, selector.Body);

        aliases[selector.Parameters[0]] = (model, subqueryAlias);
        var selectorTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
        var selectorSql = selectorTranslator.TranslateScalar(selector.Body);
        var predicates = new List<string>();
        foreach (var predicate in query.Predicates)
        {
            var translator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(translator.TranslatePredicate(predicate));
        }

        var sumSql = $"COALESCE(sum({selectorSql}), 0)";
        var resultType = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
        if (resultType == typeof(int))
        {
            sumSql += "::int";
        }
        else if (resultType == typeof(long))
        {
            sumSql += "::bigint";
        }

        var sql = $"(SELECT {sumSql} FROM {model.StoreObjectName} AS {subqueryAlias}";
        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        _sql.Push(sql + ")");
        return node;
    }

    private Expression VisitQueryableNullableAggregate(MethodCallExpression node)
    {
        if (node.Arguments.Count != 2)
        {
            throw new UnsupportedQueryExpressionException(
                $"Queryable {node.Method.Name} subqueries require a source and scalar selector.");
        }

        if (Nullable.GetUnderlyingType(node.Type) is null && !_allowNonNullableAggregateProjection)
        {
            throw new UnsupportedQueryExpressionException(
                $"Queryable {node.Method.Name} subqueries require a nullable result to preserve empty-set semantics.");
        }

        var source = RewriteCapturedQueryableRoots(node.Arguments[0]);
        source = ResolveQueryableExpression(source);
        var model = ResolveQueryModel(source);
        var query = QueryModelFactory.Create(source);
        if (query.Orderings.Count > 0 || query.Skip is not null || query.Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                $"Queryable {node.Method.Name} subqueries do not support ordering or paging.");
        }

        var aliases = new Dictionary<ParameterExpression, (EntityModel Model, string Alias)>();
        if (_entityParameter is not null)
        {
            aliases[_entityParameter] = (_model, _alias);
        }

        if (_correlatedAliases is not null)
        {
            foreach (var pair in _correlatedAliases)
            {
                aliases[pair.Key] = pair.Value;
            }
        }

        var subqueryAlias = _alias == "e" ? "s" : _alias + "s";
        var selector = UnquoteLambda(node.Arguments[1]);
        EnsureNoNullableValueDereference(node.Method.Name, selector.Body);
        var selectorType = Nullable.GetUnderlyingType(selector.ReturnType) ?? selector.ReturnType;
        if (node.Method.Name is nameof(Queryable.Min) or nameof(Queryable.Max) &&
            (selectorType == typeof(bool) || selectorType == typeof(Guid)))
        {
            throw new UnsupportedQueryExpressionException(
                $"Queryable {node.Method.Name} does not support nullable {selectorType.Name} selectors.");
        }
        aliases[selector.Parameters[0]] = (model, subqueryAlias);
        var selectorTranslator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
        var selectorSql = selectorTranslator.TranslateScalar(selector.Body);
        var predicates = new List<string>();
        foreach (var predicate in query.Predicates)
        {
            var translator = new SqlExpressionTranslator(model, _parameters, subqueryAlias, aliases, useCSharpNullSemantics: true);
            predicates.Add(translator.TranslatePredicate(predicate));
        }

        var function = node.Method.Name switch
        {
            nameof(Queryable.Min) => "min",
            nameof(Queryable.Max) => "max",
            nameof(Queryable.Average) => "avg",
            _ => throw new UnreachableException()
        };
        var aggregateSql = $"{function}({selectorSql})";
        var resultType = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
        if (node.Method.Name == nameof(Queryable.Average) && resultType == typeof(double))
        {
            aggregateSql += "::double precision";
        }

        var sql = $"(SELECT {aggregateSql} FROM {model.StoreObjectName} AS {subqueryAlias}";
        if (predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        _sql.Push(sql + ")");
        return node;
    }

    private static void EnsureNoNullableValueDereference(string aggregateName, Expression selector)
    {
        var visitor = new NullableValueDereferenceVisitor();
        visitor.Visit(selector);
        if (visitor.Found)
        {
            throw new UnsupportedQueryExpressionException(
                $"Queryable {aggregateName} selectors cannot dereference nullable values; use a nullable selector to preserve NULL semantics.");
        }
    }

    private sealed class NullableValueDereferenceVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.Name == nameof(Nullable<int>.Value) &&
                node.Expression is not null &&
                Nullable.GetUnderlyingType(node.Expression.Type) is not null)
            {
                Found = true;
            }

            return base.VisitMember(node);
        }
    }

    private static EntityModel ResolveQueryModel(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression { Value: IEntityModelSource source })
        {
            return source.Model;
        }

        if (expression is MethodCallExpression call && call.Arguments.Count > 0)
        {
            return ResolveQueryModel(call.Arguments[0]);
        }

        if (expression is MemberExpression)
        {
            try
            {
                if (ExpressionValueReader.Read(expression) is IEntityModelSource memberSource)
                {
                    return memberSource.Model;
                }
            }
            catch (UnsupportedQueryExpressionException)
            {
                // Fall through to the deterministic unsupported-source diagnostic below.
            }
        }

        throw new UnsupportedQueryExpressionException($"Cannot resolve query source '{expression}'.");
    }

    private static Expression ResolveQueryableExpression(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression or MethodCallExpression)
        {
            return expression;
        }

        try
        {
            if (ExpressionValueReader.Read(expression) is IQueryable queryable)
            {
                return queryable.Expression;
            }
        }
        catch (UnsupportedQueryExpressionException)
        {
            // Fall through to the deterministic unsupported-source diagnostic below.
        }

        throw new UnsupportedQueryExpressionException($"Cannot resolve query source '{expression}'.");
    }

    private static Expression RewriteCapturedQueryableRoots(Expression expression)
    {
        return new CapturedQueryableRootRewriter().Visit(expression) ?? expression;
    }

    private sealed class CapturedQueryableRootRewriter : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (!typeof(IQueryable).IsAssignableFrom(node.Type))
            {
                return base.VisitMember(node);
            }

            try
            {
                if (ExpressionValueReader.Read(node) is IQueryable queryable)
                {
                    return queryable.Expression;
                }
            }
            catch (UnsupportedQueryExpressionException)
            {
                // Ordinary entity members and scalar captures remain unchanged.
            }

            return base.VisitMember(node);
        }
    }

    private static Expression? TryReadAnyPredicateValue(
        Expression possibleElement,
        Expression possibleValue,
        ParameterExpression elementParameter)
    {
        possibleElement = StripConvert(possibleElement);
        return possibleElement == elementParameter ? possibleValue : null;
    }

    private bool IsEntityMember(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion)
        {
            expression = StripConvert(conversion.Arguments[0]);
        }

        return expression is MemberExpression member && member.Expression == _entityParameter;
    }

    private bool TryReadNonEntityNull(Expression expression)
    {
        if (IsEntityMember(expression))
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

    private static bool IsNullConstant(Expression expression)
    {
        expression = StripConvert(expression);
        return expression is ConstantExpression { Value: null };
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion)
        {
            return StripConvert(conversion.Arguments[0]);
        }

        return expression;
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } unary)
        {
            expression = unary.Operand;
        }

        return expression as LambdaExpression
            ?? throw new UnsupportedQueryExpressionException($"Expression '{expression}' is not a lambda.");
    }

    private static int ReadSubstringStart(Expression expression)
    {
        var value = ExpressionValueReader.Read(expression);
        return value is int start
            ? start + 1
            : throw new UnsupportedQueryExpressionException("Substring start index must be an integer.");
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
}
