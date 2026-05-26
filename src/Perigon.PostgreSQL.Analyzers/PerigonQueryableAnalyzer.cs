using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Perigon.PostgreSQL.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerigonQueryableAnalyzer : DiagnosticAnalyzer
{
    public const string FullTableMutationDiagnosticId = "PG001";
    public const string UnsupportedQueryExpressionDiagnosticId = "PG002";
    public const string DynamicJsonPocoDiagnosticId = "PG003";

    private static readonly DiagnosticDescriptor FullTableMutationRule = new(
        FullTableMutationDiagnosticId,
        "Mutation query should include a Where filter",
        "'{0}' is called without a visible Where filter; add Where(...) or pass explicit full-table options",
        "Safety",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Perigon.PostgreSQL protects full-table update/delete at runtime. This diagnostic catches the common unfiltered shape at compile time.");

    private static readonly DiagnosticDescriptor UnsupportedQueryExpressionRule = new(
        UnsupportedQueryExpressionDiagnosticId,
        "Query expression uses an unsupported method call",
        "'{0}' is not supported by Perigon.PostgreSQL SQL translation; {1}",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Perigon.PostgreSQL does not fall back to client-side query execution. Unsupported method calls in query expressions fail at runtime and should be rewritten to a supported SQL shape or raw SQL.");

    private static readonly DiagnosticDescriptor DynamicJsonPocoRule = new(
        DynamicJsonPocoDiagnosticId,
        "JSONB POCO mapping is not AOT-safe",
        "JSONB property '{0}' uses POCO type '{1}', which is not supported; use string, JsonDocument, JsonElement, or a source-generated JSON path",
        "AOT",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dynamic JSON POCO mapping requires runtime object graph handling that is outside Perigon.PostgreSQL's NativeAOT boundary.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(FullTableMutationRule, UnsupportedQueryExpressionRule, DynamicJsonPocoRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        if (!IsPerigonExtensionMethod(context, invocation))
        {
            return;
        }

        AnalyzeUnsupportedQueryExpressions(context, memberAccess.Expression);

        var methodName = memberAccess.Name.Identifier.ValueText;
        var requiredArgumentCount = methodName switch
        {
            "ToDeleteSql" => 0,
            "ExecuteDeleteAsync" => 0,
            "ToUpdateSql" => 1,
            "ExecuteUpdateAsync" => 1,
            _ => -1
        };

        if (requiredArgumentCount < 0 || invocation.ArgumentList.Arguments.Count > requiredArgumentCount)
        {
            return;
        }

        if (ContainsWhereCall(memberAccess.Expression))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(FullTableMutationRule, memberAccess.Name.GetLocation(), methodName));
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!HasJsonColumnAttribute(property))
        {
            return;
        }

        var propertyType = UnwrapNullable(property.Type);
        if (IsSupportedJsonClrType(propertyType))
        {
            return;
        }

        var location = property.Locations.Length > 0 ? property.Locations[0] : Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            DynamicJsonPocoRule,
            location,
            property.Name,
            propertyType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static bool IsPerigonExtensionMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            return false;
        }

        var reducedFrom = symbol.ReducedFrom ?? symbol;
        return reducedFrom.ContainingType.ToDisplayString() == "Perigon.PostgreSQL.PostgresQueryableExtensions";
    }

    private static void AnalyzeUnsupportedQueryExpressions(SyntaxNodeAnalysisContext context, ExpressionSyntax queryExpression)
    {
        foreach (var lambda in queryExpression.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            foreach (var invocation in lambda.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (!TryGetUnsupportedInvocation(context, invocation, out var methodName, out var suggestion))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedQueryExpressionRule,
                    invocation.GetLocation(),
                    methodName,
                    suggestion));
            }
        }
    }

    private static bool TryGetUnsupportedInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        out string methodName,
        out string suggestion)
    {
        methodName = "method call";
        suggestion = "rewrite it using supported LINQ members, Perigon PostgreSQL extensions, or raw SQL";

        if (IsExpressionInvoke(context, invocation, out methodName))
        {
            suggestion = "inline the predicate body instead of using Expression.Invoke";
            return true;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            return false;
        }

        var reducedFrom = symbol.ReducedFrom ?? symbol;
        methodName = $"{reducedFrom.ContainingType.Name}.{reducedFrom.Name}";

        if (IsUnsupportedStringOverload(reducedFrom))
        {
            suggestion = "culture-aware and StringComparison overloads are not translated; use a supported string method or explicit PostgreSQL collation/raw SQL";
            return true;
        }

        if (reducedFrom.ContainingType.ToDisplayString() == "System.Math")
        {
            suggestion = "System.Math calls are not translated yet; use a supported comparison/expression shape or raw SQL";
            return true;
        }

        if (IsUserDefinedMethod(reducedFrom))
        {
            suggestion = "local methods are not translated; inline the expression or expose a dedicated SQL translation API";
            return true;
        }

        return false;
    }

    private static bool IsExpressionInvoke(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        out string methodName)
    {
        methodName = "Expression.Invoke";
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name.Identifier.ValueText != "Invoke")
        {
            return false;
        }

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (receiverType is null)
        {
            return false;
        }

        return receiverType.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";
    }

    private static bool IsUnsupportedStringOverload(IMethodSymbol method)
    {
        if (method.ContainingType.SpecialType != SpecialType.System_String &&
            method.ContainingType.ToDisplayString() != "string")
        {
            return false;
        }

        foreach (var parameter in method.Parameters)
        {
            var parameterType = parameter.Type.ToDisplayString();
            if (parameterType == "System.StringComparison" || parameterType == "System.Globalization.CultureInfo")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUserDefinedMethod(IMethodSymbol method)
    {
        var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? "";
        return containingNamespace.Length == 0 ||
            (!containingNamespace.StartsWith("System", StringComparison.Ordinal) &&
             !containingNamespace.StartsWith("Perigon.PostgreSQL", StringComparison.Ordinal) &&
             !containingNamespace.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    private static bool HasJsonColumnAttribute(IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Perigon.PostgreSQL.Attributes.ColumnAttribute")
            {
                continue;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key != "TypeName" || namedArgument.Value.Value is not string typeName)
                {
                    continue;
                }

                var normalized = typeName.ToLowerInvariant();
                if (normalized.Contains("json"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            namedType.TypeArguments.Length == 1)
        {
            return namedType.TypeArguments[0];
        }

        return type;
    }

    private static bool IsSupportedJsonClrType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        var typeName = type.ToDisplayString();
        return typeName == "System.Text.Json.JsonDocument" || typeName == "System.Text.Json.JsonElement";
    }

    private static bool ContainsWhereCall(ExpressionSyntax expression)
    {
        expression = StripParentheses(expression);
        if (expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Name.Identifier.ValueText == "Where")
            {
                return true;
            }

            return ContainsWhereCall(memberAccess.Expression);
        }

        if (expression is MemberAccessExpressionSyntax nested)
        {
            return ContainsWhereCall(nested.Expression);
        }

        return false;
    }

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }
}