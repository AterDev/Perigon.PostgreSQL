using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Expressions;

internal sealed class SqlExpressionTranslator : ExpressionVisitor
{
    private readonly EntityModel _model;
    private readonly ParameterBag _parameters;
    private readonly string _alias;
    private readonly Stack<string> _sql = new();
    private ParameterExpression? _entityParameter;

    public SqlExpressionTranslator(EntityModel model, ParameterBag parameters, string alias)
    {
        _model = model;
        _parameters = parameters;
        _alias = alias;
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

        if (IsNullConstant(node.Right) || TryReadNonEntityNull(node.Right))
        {
            Visit(node.Left);
            var left = _sql.Pop();
            _sql.Push(node.NodeType == ExpressionType.Equal ? $"{left} IS NULL" : $"{left} IS NOT NULL");
            return node;
        }

        if (IsNullConstant(node.Left) || TryReadNonEntityNull(node.Left))
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

        if (node.Method.Name == nameof(string.EndsWith) && node.Object is not null)
        {
            Visit(node.Object);
            var instance = _sql.Pop();
            var value = ExpressionValueReader.Read(node.Arguments[0]);
            _sql.Push($"{instance} LIKE {_parameters.Add("%" + value)}");
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

        if (node.Method.Name == nameof(string.ToLower) && node.Object is not null)
        {
            Visit(node.Object);
            _sql.Push($"lower({_sql.Pop()})");
            return node;
        }

        if (node.Method.Name == nameof(string.ToUpper) && node.Object is not null)
        {
            Visit(node.Object);
            _sql.Push($"upper({_sql.Pop()})");
            return node;
        }

        if (node.Method.Name == nameof(Enumerable.Contains))
        {
            return VisitContains(node);
        }

        if (node.Method.Name == nameof(Enumerable.Any))
        {
            return VisitEnumerableAny(node);
        }

        if (node.Method.Name == nameof(Enumerable.All))
        {
            return VisitEnumerableAll(node);
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
            _sql.Push($"{column} @> {_parameters.Add(ExpressionValueReader.Read(valueExpression))}");
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
        if (StripConvert(lambda.Body) is not BinaryExpression { NodeType: ExpressionType.Equal } binary)
        {
            throw new UnsupportedQueryExpressionException("Array Any currently supports equality predicates only.");
        }

        var valueExpression = TryReadAnyPredicateValue(binary.Left, binary.Right, lambda.Parameters[0])
            ?? TryReadAnyPredicateValue(binary.Right, binary.Left, lambda.Parameters[0])
            ?? throw new UnsupportedQueryExpressionException("Array Any predicate must compare the element to a value.");
        _sql.Push($"{_parameters.Add(ExpressionValueReader.Read(valueExpression))} = ANY({column})");
        return node;
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
