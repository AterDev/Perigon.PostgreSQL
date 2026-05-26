using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using Perigon.PostgreSQL.SourceGeneration;

namespace Perigon.PostgreSQL.Tests.SourceGeneration;

public sealed class EntityModelGeneratorTests
{
    [Fact]
    public void Generator_registers_dbset_entities_with_table_and_column_metadata()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using Perigon.PostgreSQL.Attributes;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<DemoUser> Users => Set<DemoUser>();
            }

            [Table("demo_users", Schema = "demo")]
            public sealed class DemoUser
            {
                public int Id { get; set; }

                [Column("display_name")]
                public string Name { get; set; } = "";

                [Column(TypeName = "jsonb")]
                public string? ProfileJson { get; set; }

                [NotMapped]
                public string Ignored { get; set; } = "";
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("EntityModelRegistry.Register<global::Demo.DemoUser>", generated);
        Assert.Contains("\"demo\"", generated);
        Assert.Contains("\"demo_users\"", generated);
        Assert.Contains("\"display_name\"", generated);
        Assert.Contains("\"jsonb\"", generated);
        Assert.DoesNotContain("Ignored", generated);
        Assert.Contains("EntityValueAccessorRegistry.Register<global::Demo.DemoUser>", generated);
    }

    [Fact]
    public void Generator_registers_materializers_for_accessible_projection_types()
    {
        const string source = """
            using Perigon.PostgreSQL;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<DemoUser> Users => Set<DemoUser>();
            }

            public sealed class DemoUser
            {
                public int Id { get; set; }

                public string UserName { get; set; } = "";
            }

            public sealed class UserProjection
            {
                public int Id { get; set; }

                public string UserName { get; set; } = "";
            }

            public abstract class AbstractProjection
            {
                public int Id { get; set; }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("EntityMaterializerRegistry.Register<global::Demo.UserProjection>", generated);
        Assert.Contains("private static global::Demo.UserProjection Materialize_", generated);
        Assert.DoesNotContain("AbstractProjection", generated);
    }

    private static string RunGenerator(string source, out ImmutableArray<Diagnostic> diagnostics)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTests",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new EntityModelGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        diagnostics = outputCompilation.GetDiagnostics();
        var runResult = driver.GetRunResult();
        var generatedTree = Assert.Single(runResult.GeneratedTrees);
        return generatedTree.ToString();
    }

    private static MetadataReference[] GetMetadataReferences()
    {
        var platformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList()
            ?? [];

        platformAssemblies.Add(MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));
        return platformAssemblies
            .GroupBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }
}