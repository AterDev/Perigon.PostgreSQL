using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Perigon.PostgreSQL.Analyzers;
using Perigon.PostgreSQL.SourceGeneration;

namespace Perigon.PostgreSQL.Tests.Analyzers;

public sealed class UninitializedDbSetDiagnosticSuppressorTests
{
    [Fact]
    public async Task Source_generated_dbset_property_does_not_report_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public sealed class TestContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Dbset_property_with_explicit_constructor_does_not_report_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;
            using Perigon.PostgreSQL.Options;

            public sealed class User { public int Id { get; set; } }

            public sealed class TestContext : DbContext
            {
                public TestContext(DbContextOptions<TestContext> options) : base(options) { }

                public DbSet<User> Users { get; set; }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Non_dbset_property_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            public sealed class Plain
            {
                public string Name { get; set; }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Dbset_property_outside_dbcontext_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public sealed class NotAContext
            {
                public DbSet<User> Users { get; set; }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Explicit_constructor_still_reports_cs8618_for_ordinary_property_and_field()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public sealed class MixedContext : DbContext
            {
                public MixedContext() { }

                public DbSet<User> Users { get; set; }
                public string Name { get; set; }
                public string Label;
            }
            """);

        var warnedMembers = diagnostics
            .Where(diagnostic => diagnostic.Id == "CS8618")
            .Select(diagnostic => Assert.Single(diagnostic.AdditionalLocations))
            .Select(location => location.SourceTree!.GetText().ToString(location.SourceSpan))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Label", "Name" }, warnedMembers);
    }

    [Fact]
    public async Task Dbset_property_without_generator_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public sealed class TestContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """, runGenerator: false);

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Virtual_dbset_property_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public class TestContext : DbContext
            {
                public virtual DbSet<User> Users { get; set; }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Abstract_entity_dbset_property_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public abstract class User { public int Id { get; set; } }

            public sealed class TestContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Not_mapped_entity_dbset_property_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;
            using System.ComponentModel.DataAnnotations.Schema;

            [NotMapped]
            public sealed class User { public int Id { get; set; } }

            public sealed class TestContext : DbContext
            {
                public DbSet<User> Users { get; set; }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    [Fact]
    public async Task Inaccessible_nested_context_dbset_property_still_reports_cs8618()
    {
        var diagnostics = await AnalyzeAsync("""
            using Perigon.PostgreSQL;

            public sealed class User { public int Id { get; set; } }

            public static class Contexts
            {
                private sealed class TestContext : DbContext
                {
                    public DbSet<User> Users { get; set; }
                }
            }
            """);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8618");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool runGenerator = true)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
            .Cast<MetadataReference>()
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(Perigon.PostgreSQL.DbContext).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "SuppressorTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        Compilation analyzedCompilation = compilation;
        if (runGenerator)
        {
            var generatorDriver = CSharpGeneratorDriver.Create(
                [new EntityModelGenerator().AsSourceGenerator()],
                parseOptions: (CSharpParseOptions)syntaxTree.Options);
            generatorDriver.RunGeneratorsAndUpdateCompilation(compilation, out analyzedCompilation, out var generatorDiagnostics);
            Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        var suppressor = new UninitializedDbSetDiagnosticSuppressor();
        var compilationWithAnalyzers = analyzedCompilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(suppressor));
        var diagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Id == "AD0001");
        return diagnostics;
    }
}
