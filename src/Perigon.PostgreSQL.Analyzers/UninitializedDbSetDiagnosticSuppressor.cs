using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Perigon.PostgreSQL.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UninitializedDbSetDiagnosticSuppressor : DiagnosticSuppressor
{
    public const string SuppressionId = "PERIGON0001";

    private const string NullablePropertyWarningId = "CS8618";

    private static readonly SuppressionDescriptor DbSetInitializerDescriptor = new(
        SuppressionId,
        NullablePropertyWarningId,
        "Perigon.PostgreSQL initializes source-generated DbSet properties when the context is created.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions =>
        ImmutableArray.Create(DbSetInitializerDescriptor);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var dbSetType = context.Compilation.GetTypeByMetadataName("Perigon.PostgreSQL.DbSet`1");
        if (dbSetType is null)
        {
            return;
        }

        HashSet<IPropertySymbol>? initializedProperties = null;
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Id != NullablePropertyWarningId)
            {
                continue;
            }

            // With an explicit constructor, the primary location points to the constructor.
            var location = diagnostic.AdditionalLocations.Count > 0
                ? diagnostic.AdditionalLocations[0]
                : diagnostic.Location;
            if (location.SourceTree is not { } tree ||
                tree.GetRoot(context.CancellationToken).FindNode(location.SourceSpan, getInnermostNodeForTie: true)
                    .FirstAncestorOrSelf<PropertyDeclarationSyntax>() is not { } declaration ||
                declaration.Identifier.Span != location.SourceSpan ||
                context.GetSemanticModel(tree).GetDeclaredSymbol(declaration, context.CancellationToken) is not IPropertySymbol property ||
                !SymbolEqualityComparer.Default.Equals(property.Type.OriginalDefinition, dbSetType))
            {
                continue;
            }

            initializedProperties ??= GetGeneratedDbSetProperties(context);
            if (initializedProperties.Contains(property))
            {
                context.ReportSuppression(Suppression.Create(DbSetInitializerDescriptor, diagnostic));
            }
        }
    }

    private static HashSet<IPropertySymbol> GetGeneratedDbSetProperties(SuppressionAnalysisContext context)
    {
        // The generated assignments are the source of truth for which properties get initialized.
        var properties = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        var registration = context.Compilation.Assembly.GetTypeByMetadataName(
            "Perigon.PostgreSQL.Generated.PerigonEntityModelRegistration");
        if (registration is null)
        {
            return properties;
        }

        var registry = context.Compilation.GetTypeByMetadataName("Perigon.PostgreSQL.Metadata.EntityModelRegistry");
        foreach (var reference in registration.DeclaringSyntaxReferences)
        {
            var model = context.GetSemanticModel(reference.SyntaxTree);
            foreach (var invocation in reference.GetSyntax(context.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "RegisterDbSetInitializer" } ||
                    invocation.ArgumentList.Arguments.Count != 1 ||
                    invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax { Body: BlockSyntax body } ||
                    model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
                    !SymbolEqualityComparer.Default.Equals(method.ContainingType, registry))
                {
                    continue;
                }

                foreach (var statement in body.Statements)
                {
                    if (statement is ExpressionStatementSyntax
                        {
                            Expression: AssignmentExpressionSyntax { Right: InvocationExpressionSyntax value } assignment
                        } &&
                        model.GetSymbolInfo(value, context.CancellationToken).Symbol is IMethodSymbol { Name: "CreateGeneratedDbSet" } &&
                        model.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is IPropertySymbol property)
                    {
                        properties.Add(property);
                    }
                }
            }
        }

        return properties;
    }
}
