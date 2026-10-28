using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Perigon.PostgreSQL.Expressions;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Sql;

/// <summary>
/// Caches SQL and parameter slots for a conservative entity-select subset,
/// keyed by expression identity. Values are read again on every execution.
/// </summary>
internal static class SimpleQueryTemplateCache
{
    private static readonly ConditionalWeakTable<Expression, CacheEntry> Entries = new();
    private static readonly object EntryCreationSync = new();

    public static bool TryBuild(EntityModel model, Expression expression, out BoundSql sql)
    {
        if (!Entries.TryGetValue(expression, out var entry))
        {
            lock (EntryCreationSync)
            {
                if (!Entries.TryGetValue(expression, out _))
                {
                    Entries.Add(expression, new CacheEntry(model));
                }
            }
            if (RebuiltQueryTemplateCache.TryBuild(model, expression, out sql))
            {
                return true;
            }
            sql = null!;
            return false;
        }

        if (!ReferenceEquals(entry.Model, model))
        {
            sql = null!;
            return false;
        }

        lock (entry.Sync)
        {
            if (entry.Unsupported)
            {
                sql = null!;
                return false;
            }

            if (entry.Template is null)
            {
                if (!SimpleQueryParser.TryParse(model, expression, out var parsed))
                {
                    entry.Unsupported = true;
                    sql = null!;
                    return false;
                }

                entry.Template = new CachedTemplate(Render(model, parsed), parsed.ParameterExpressions);
            }

            if (!TryReadParameters(entry.Template.ParameterExpressions, out var values))
            {
                sql = null!;
                return false;
            }

            sql = new BoundSql(
                entry.Template.CommandText,
                values.Select((value, index) => new SqlParameterValue(index + 1, value)).ToArray());
            return true;
        }
    }

    private static bool TryReadParameters(IReadOnlyList<Expression> expressions, out object?[] values)
    {
        values = new object?[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
        {
            try
            {
                values[i] = ExpressionValueReader.Read(expressions[i]);
            }
            catch (UnsupportedQueryExpressionException)
            {
                values = [];
                return false;
            }

            if (values[i] is null)
            {
                values = [];
                return false;
            }
        }

        return true;
    }

    private sealed class CacheEntry
    {
        public CacheEntry(EntityModel model) => Model = model;
        public object Sync { get; } = new();
        public EntityModel Model { get; }
        public CachedTemplate? Template { get; set; }
        public bool Unsupported { get; set; }
    }

    private sealed record CachedTemplate(string CommandText, IReadOnlyList<Expression> ParameterExpressions);

    private static string Render(EntityModel model, ParsedQuery query)
    {
        var columns = string.Join(", ", model.Columns.Select(c => $"e.{Identifier.Quote(c.ColumnName)}"));
        var sql = $"SELECT {columns} FROM {model.StoreObjectName} AS e";
        if (query.Predicates.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", query.Predicates.Select(RenderPredicate));
        }

        if (query.Orderings.Count > 0)
        {
            sql += " ORDER BY " + string.Join(", ", query.Orderings.Select(ordering =>
                $"e.{Identifier.Quote(ordering.PropertyName)} {(ordering.Descending ? "DESC" : "ASC")}"));
        }

        if (query.Take is not null)
        {
            sql += " LIMIT $" + query.Take.Position;
        }

        if (query.Skip is not null)
        {
            sql += " OFFSET $" + query.Skip.Position;
        }

        return sql;
    }

    private static string RenderPredicate(SimplePredicate predicate)
    {
        if (predicate.Kind == SimplePredicateKind.BooleanMember)
        {
            return $"e.{Identifier.Quote(predicate.PropertyName)}";
        }

        if (predicate.Kind == SimplePredicateKind.Logical)
        {
            var logicalOperator = predicate.LogicalOperator == ExpressionType.AndAlso ? "AND" : "OR";
            return $"({RenderPredicate(predicate.Left!)} {logicalOperator} {RenderPredicate(predicate.Right!)})";
        }

        var entity = $"e.{Identifier.Quote(predicate.PropertyName)}";
        var parameter = "$" + predicate.Value!.Position;
        var operation = predicate.Operator switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new InvalidOperationException()
        };

        var left = predicate.ValueOnLeft ? parameter : entity;
        var right = predicate.ValueOnLeft ? entity : parameter;
        return $"({left} {operation} {right})";
    }

    private sealed class SimpleQueryParser
    {
        public static bool TryParse(EntityModel model, Expression expression, out ParsedQuery query)
        {
            var parser = new SimpleQueryParser(model);
            if (!parser.Visit(expression) || !parser.EvaluateValues())
            {
                query = null!;
                return false;
            }

            query = parser.Build();
            return true;
        }

        private readonly EntityModel _model;
        private readonly List<SimplePredicate> _predicates = [];
        private readonly List<SimpleOrdering> _orderings = [];
        private readonly List<string> _shape = [];
        private Expression? _takeExpression;
        private Expression? _skipExpression;
        private object? _takeValue;
        private object? _skipValue;

        private SimpleQueryParser(EntityModel model)
        {
            _model = model;
            _shape.Add(model.StoreObjectName);
            foreach (var column in model.Columns)
            {
                _shape.Add($"c:{column.PropertyName}:{column.ColumnName}:{column.IsWritable}");
            }
        }

        private ParsedQuery Build()
        {
            var positions = 1;
            foreach (var predicate in _predicates)
            {
                AssignParameterSlots(predicate, ref positions);
            }

            ParameterSlot? take = null;
            ParameterSlot? skip = null;
            if (_takeExpression is not null)
            {
                take = new ParameterSlot(positions++);
            }

            if (_skipExpression is not null)
            {
                skip = new ParameterSlot(positions++);
            }

            _shape.Add($"take:{_takeExpression?.Type.FullName ?? "none"}");
            _shape.Add($"skip:{_skipExpression?.Type.FullName ?? "none"}");
            var values = _predicates.SelectMany(CollectValues)
                .Concat(_takeExpression is null ? [] : [_takeValue])
                .Concat(_skipExpression is null ? [] : [_skipValue])
                .ToArray();
            var parameterExpressions = _predicates.SelectMany(CollectExpressions)
                .Concat(_takeExpression is null ? [] : [_takeExpression])
                .Concat(_skipExpression is null ? [] : [_skipExpression])
                .ToArray();
            return new ParsedQuery(
                string.Join("|", _shape.Select(static item => $"{item.Length}:{item}")),
                _predicates,
                _orderings,
                values,
                take,
                skip,
                parameterExpressions);
        }

        private bool Visit(Expression expression)
        {
            if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable))
            {
                return expression is ConstantExpression;
            }

            if (!Visit(call.Arguments[0]))
            {
                return false;
            }

            switch (call.Method.Name)
            {
                case nameof(Queryable.Where):
                    return AddPredicate(Unquote(call.Arguments[1]));
                case nameof(Queryable.OrderBy):
                    return AddOrdering(Unquote(call.Arguments[1]), false);
                case nameof(Queryable.OrderByDescending):
                    return AddOrdering(Unquote(call.Arguments[1]), true);
                case nameof(Queryable.ThenBy):
                    return AddOrdering(Unquote(call.Arguments[1]), false);
                case nameof(Queryable.ThenByDescending):
                    return AddOrdering(Unquote(call.Arguments[1]), true);
                case nameof(Queryable.Take):
                    if (call.Arguments[1].Type != typeof(int) || !CanReadValueExpression(call.Arguments[1])) return false;
                    _takeExpression = call.Arguments[1];
                    _shape.Add("take");
                    return true;
                case nameof(Queryable.Skip):
                    if (call.Arguments[1].Type != typeof(int) || !CanReadValueExpression(call.Arguments[1])) return false;
                    _skipExpression = call.Arguments[1];
                    _shape.Add("skip");
                    return true;
                default:
                    return false;
            }
        }

        private bool AddOrdering(LambdaExpression lambda, bool descending)
        {
            if (lambda.Parameters.Count != 1 || !TryGetEntityMember(lambda.Body, lambda.Parameters[0], out var propertyName)) return false;
            _orderings.Add(new SimpleOrdering(propertyName, descending));
            _shape.Add($"order:{propertyName}:{descending}");
            return true;
        }

        private bool AddPredicate(LambdaExpression lambda)
        {
            if (lambda.Parameters.Count != 1 || lambda.ReturnType != typeof(bool) || !TryReadPredicate(lambda.Body, lambda.Parameters[0], out var predicate)) return false;
            _predicates.Add(predicate);
            _shape.Add(predicate.Shape);
            return true;
        }

        private bool TryReadPredicate(Expression expression, ParameterExpression parameter, out SimplePredicate predicate)
        {
            if (expression is MemberExpression member && TryGetEntityMember(member, parameter, out var booleanProperty))
            {
                if (expression.Type != typeof(bool))
                {
                    predicate = null!;
                    return false;
                }

                predicate = SimplePredicate.Boolean(booleanProperty);
                return true;
            }

            if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical)
            {
                if (!TryReadPredicate(logical.Left, parameter, out var left) || !TryReadPredicate(logical.Right, parameter, out var right))
                {
                    predicate = null!;
                    return false;
                }

                predicate = SimplePredicate.Logical(logical.NodeType, left, right);
                return true;
            }

            if (expression is BinaryExpression binary &&
                binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual)
            {
                var operand = TryGetEntityMember(binary.Left, parameter, out var leftProperty)
                    ? (binary.Left, leftProperty, binary.Right)
                    : TryGetEntityMember(binary.Right, parameter, out var rightProperty)
                        ? (binary.Right, rightProperty, binary.Left)
                        : default;
                if (operand.Item2 is not null && CanReadValueExpression(operand.Item3))
                {
                    var valueOnLeft = !TryGetEntityMember(binary.Left, parameter, out _);
                    predicate = SimplePredicate.Binary(operand.Item2, binary.NodeType, operand.Item3, valueOnLeft);
                    return true;
                }
            }

            predicate = null!;
            return false;
        }

        private static bool TryReadValue(Expression expression, out object? value)
        {
            try
            {
                value = ExpressionValueReader.Read(expression);
                return true;
            }
            catch (UnsupportedQueryExpressionException)
            {
                value = null;
                return false;
            }
        }

        private bool EvaluateValues()
        {
            foreach (var predicate in _predicates)
            {
                if (!EvaluatePredicate(predicate)) return false;
            }

            if (_takeExpression is not null && (!TryReadValue(_takeExpression, out _takeValue) || _takeValue is null)) return false;
            if (_skipExpression is not null && (!TryReadValue(_skipExpression, out _skipValue) || _skipValue is null)) return false;
            return true;
        }

        private static bool EvaluatePredicate(SimplePredicate predicate)
        {
            if (predicate.Kind == SimplePredicateKind.Logical)
            {
                return EvaluatePredicate(predicate.Left!) && EvaluatePredicate(predicate.Right!);
            }

            if (predicate.ValueExpression is null) return true;
            return TryReadValue(predicate.ValueExpression, out var value) && value is not null && predicate.SetRuntimeValue(value);
        }

        private static bool CanReadValueExpression(Expression expression)
        {
            expression = StripConvert(expression);
            return expression switch
            {
                ConstantExpression => true,
                MemberExpression member => member.Member is FieldInfo && (member.Expression is null || CanReadValueExpression(member.Expression)),
                NewArrayExpression array => array.Expressions.All(CanReadValueExpression),
                MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion => CanReadValueExpression(conversion.Arguments[0]),
                _ => false
            };
        }

        private static Expression StripConvert(Expression expression)
        {
            while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        private static void AssignParameterSlots(SimplePredicate predicate, ref int position)
        {
            if (predicate.Kind == SimplePredicateKind.Logical)
            {
                AssignParameterSlots(predicate.Left!, ref position);
                AssignParameterSlots(predicate.Right!, ref position);
            }
            else if (predicate.ValueExpression is not null)
            {
                predicate.Value = new ParameterSlot(position++);
            }
        }

        private static IEnumerable<object?> CollectValues(SimplePredicate predicate)
        {
            if (predicate.Kind == SimplePredicateKind.Logical)
            {
                return CollectValues(predicate.Left!).Concat(CollectValues(predicate.Right!));
            }

            return predicate.RuntimeValue is null ? [] : [predicate.RuntimeValue];
        }

        private static IEnumerable<Expression> CollectExpressions(SimplePredicate predicate)
        {
            if (predicate.Kind == SimplePredicateKind.Logical)
            {
                return CollectExpressions(predicate.Left!).Concat(CollectExpressions(predicate.Right!));
            }

            return predicate.ValueExpression is null ? [] : [predicate.ValueExpression];
        }

        private bool TryGetEntityMember(Expression expression, ParameterExpression parameter, out string propertyName)
        {
            if (expression is MemberExpression member && member.Expression == parameter)
            {
                try
                {
                    // The cache parser starts from CLR member access, but the
                    // rendered SQL must use the mapped store column. This is
                    // especially important for conventions such as UserName ->
                    // user_name and for source-generated NativeAOT metadata.
                    propertyName = _model.GetColumn(member.Member.Name).ColumnName;
                    return true;
                }
                catch (InvalidOperationException)
                {
                }
            }

            propertyName = "";
            return false;
        }

        private static LambdaExpression Unquote(Expression expression)
        {
            return expression is UnaryExpression { NodeType: ExpressionType.Quote } unary
                ? (LambdaExpression)unary.Operand
                : (LambdaExpression)expression;
        }
    }

    private sealed record ParsedQuery(
        string ShapeKey,
        IReadOnlyList<SimplePredicate> Predicates,
        IReadOnlyList<SimpleOrdering> Orderings,
        IReadOnlyList<object?> Values,
        ParameterSlot? Take,
        ParameterSlot? Skip,
        IReadOnlyList<Expression> ParameterExpressions);

    private sealed record SimpleOrdering(string PropertyName, bool Descending);

    private sealed class SimplePredicate
    {
        public string PropertyName { get; private init; } = "";
        public ExpressionType Operator { get; private init; }
        public Expression? ValueExpression { get; private init; }
        public object? RuntimeValue { get; private set; }
        public bool ValueOnLeft { get; private init; }
        public ParameterSlot? Value { get; set; }
        public SimplePredicate? Left { get; private init; }
        public SimplePredicate? Right { get; private init; }
        public ExpressionType LogicalOperator { get; private init; }
        public SimplePredicateKind Kind { get; private init; }
        public string Shape { get; private init; } = "";

        public static SimplePredicate Boolean(string property) => new() { PropertyName = property, Kind = SimplePredicateKind.BooleanMember, Shape = $"bool:{property}" };
        public static SimplePredicate Binary(string property, ExpressionType operation, Expression value, bool valueOnLeft) => new() { PropertyName = property, Operator = operation, ValueExpression = value, ValueOnLeft = valueOnLeft, Kind = SimplePredicateKind.Binary, Shape = $"bin:{property}:{operation}:{value.Type.FullName}:{valueOnLeft}" };
        public static SimplePredicate Logical(ExpressionType operation, SimplePredicate left, SimplePredicate right) => new() { Kind = SimplePredicateKind.Logical, LogicalOperator = operation, Left = left, Right = right, Shape = $"logical:{operation}:{left.Shape}:{right.Shape}" };

        public bool SetRuntimeValue(object value)
        {
            RuntimeValue = value;
            return true;
        }
    }

    private sealed record ParameterSlot(int Position);

    private enum SimplePredicateKind
    {
        BooleanMember,
        Binary,
        Logical
    }
}
