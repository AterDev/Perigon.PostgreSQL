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
        Assert.Contains("EntityModelRegistry.RegisterContext<global::Demo.DemoDbContext>", generated);
        Assert.Contains("EntityModel.For<global::Demo.DemoUser>()", generated);
    }

    [Fact]
    public void Generator_reads_standard_data_annotations_metadata()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using System.ComponentModel.DataAnnotations;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<DemoUser> Users => Set<DemoUser>();
            }

            [Table("demo_users", Schema = "demo")]
            public sealed class DemoUser
            {
                [Key]
                [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
                [Column("user_id")]
                public int Id { get; set; }

                [Required]
                [MaxLength(200)]
                [Column("email", TypeName = "text")]
                public string Email { get; set; } = "";

                [NotMapped]
                public string Ignored { get; set; } = "";
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("\"demo\"", generated);
        Assert.Contains("\"demo_users\"", generated);
        Assert.Contains("\"user_id\"", generated);
        Assert.Contains("\"email\"", generated);
        Assert.Contains("false,", generated);
        Assert.Contains("200,", generated);
        Assert.DoesNotContain("Ignored", generated);
    }

    [Fact]
    public void Generator_reads_view_and_ef_index_metadata_by_name()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using Perigon.PostgreSQL.Attributes;
            using Microsoft.EntityFrameworkCore;

            namespace Microsoft.EntityFrameworkCore
            {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
                public sealed class IndexAttribute : System.Attribute
                {
                    public IndexAttribute(params string[] propertyNames) => PropertyNames = propertyNames;
                    public System.Collections.Generic.IReadOnlyList<string> PropertyNames { get; }
                    public string? Name { get; init; }
                    public bool IsUnique { get; init; }
                }
            }

            namespace Demo
            {
                public sealed class DemoDbContext : DbContext
                {
                    public DemoDbContext() : base(_ => { }) { }

                    public DbSet<DemoUser> Users => Set<DemoUser>();
                    public DbSet<DemoUserView> UserViews => Set<DemoUserView>();
                }

                [Microsoft.EntityFrameworkCore.Index(nameof(Email), Name = "uq_demo_users_email", IsUnique = true)]
                public sealed class DemoUser
                {
                    public int Id { get; set; }
                    public string Email { get; set; } = "";
                }

                [View("demo_user_view", Schema = "reporting")]
                public sealed class DemoUserView
                {
                    public int Id { get; set; }
                    public string Email { get; set; } = "";
                }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("new global::Perigon.PostgreSQL.Metadata.IndexDefinition(", generated);
        Assert.Contains("\"uq_demo_users_email\"", generated);
        Assert.Contains("\"Email\"", generated);
        Assert.Contains("true)", generated);
        Assert.Contains("\"reporting\"", generated);
        Assert.Contains("\"demo_user_view\"", generated);
    }

    [Fact]
    public void Generator_reads_built_in_index_metadata_without_ef_package()
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

            [Index(nameof(Email), Name = "uq_demo_users_email", IsUnique = true)]
            public sealed class DemoUser
            {
                public int Id { get; set; }
                public string Email { get; set; } = "";
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("new global::Perigon.PostgreSQL.Metadata.IndexDefinition(", generated);
        Assert.Contains("\"uq_demo_users_email\"", generated);
        Assert.Contains("\"Email\"", generated);
        Assert.Contains("true)", generated);
    }

    [Fact]
    public void Generator_skips_not_mapped_dbset_entity_types()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<MappedUser> Users => Set<MappedUser>();
                public DbSet<IgnoredUser> IgnoredUsers => Set<IgnoredUser>();
            }

            public sealed class MappedUser
            {
                public int Id { get; set; }
            }

            [NotMapped]
            public sealed class IgnoredUser
            {
                public int Id { get; set; }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("EntityModelRegistry.Register<global::Demo.MappedUser>", generated);
        Assert.DoesNotContain("EntityModelRegistry.Register<global::Demo.IgnoredUser>", generated);
        Assert.DoesNotContain("EntityModel.For<global::Demo.IgnoredUser>()", generated);
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