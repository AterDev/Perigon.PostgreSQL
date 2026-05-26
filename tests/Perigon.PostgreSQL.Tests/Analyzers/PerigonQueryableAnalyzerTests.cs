using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Perigon.PostgreSQL.Analyzers;
using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.Tests.Analyzers;

public sealed class PerigonQueryableAnalyzerTests
{
    [Fact]
    public async Task Local_method_call_in_perigon_query_reports_unsupported_linq_warning()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Linq;
            using Perigon.PostgreSQL;

            public sealed class User
            {
                public int Age { get; set; }
            }

            public sealed class Queries
            {
                public void Run(IQueryable<User> users)
                {
                    _ = users.Where(u => IsAdult(u.Age)).ToQuerySql();
                }

                private static bool IsAdult(int age) => age >= 18;
            }
            """);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == PerigonQueryableAnalyzer.UnsupportedQueryExpressionDiagnosticId);
    }

    [Fact]
    public async Task Culture_aware_string_call_in_perigon_query_reports_unsupported_linq_warning()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Linq;
            using Perigon.PostgreSQL;

            public sealed class User
            {
                public string Name { get; set; } = "";
            }

            public sealed class Queries
            {
                public void Run(IQueryable<User> users)
                {
                    _ = users.Where(u => u.Name.Contains("admin", StringComparison.OrdinalIgnoreCase)).ToQuerySql();
                }
            }
            """);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == PerigonQueryableAnalyzer.UnsupportedQueryExpressionDiagnosticId);
    }

    [Fact]
    public async Task Jsonb_poco_column_reports_dynamic_json_warning()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL.Attributes;

            public sealed class UserProfile
            {
                public string Name { get; set; } = "";
            }

            public sealed class User
            {
                [Column(TypeName = "jsonb")]
                public UserProfile Profile { get; set; } = new();
            }
            """);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == PerigonQueryableAnalyzer.DynamicJsonPocoDiagnosticId);
    }

    [Fact]
    public async Task Jsonb_string_column_does_not_report_dynamic_json_warning()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL.Attributes;

            public sealed class User
            {
                [Column(TypeName = "jsonb")]
                public string? ProfileJson { get; set; }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Id == PerigonQueryableAnalyzer.DynamicJsonPocoDiagnosticId);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .Cast<MetadataReference>()
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(ColumnAttribute).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new PerigonQueryableAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}