using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using Perigon.PostgreSQL.SourceGeneration;

namespace Perigon.PostgreSQL.Tests.SourceGeneration;

public sealed class EntityModelGeneratorTests
{
    [Fact]
    public void Generator_registers_convention_foreign_keys_without_reflection()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using System.Collections.Generic;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }

            public sealed class User
            {
                public int Id { get; set; }
                public List<Blog> Blogs { get; set; } = new();
            }

            public sealed class Blog
            {
                public int Id { get; set; }
                public int UserId { get; set; }
                public User User { get; set; } = null!;
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("EntityModelRegistry.RegisterGeneratedForeignKey<global::Demo.Blog, global::Demo.User>", generated);
        Assert.Contains("\"UserId\", \"Id\"", generated);
        Assert.Contains("NavigationIncludeRegistry.Register<global::Demo.Blog>", generated);
        Assert.Contains("LoadReferenceAsync<global::Demo.Blog, global::Demo.User, int>", generated);
        Assert.Contains("dependent.User = principal!", generated);
        Assert.Contains("\"User\", navigationExpression, static (dependent, principal)", generated);
        Assert.Contains("NavigationIncludeRegistry.Register<global::Demo.User>", generated);
        Assert.Contains("LoadCollectionAsync<global::Demo.User, global::Demo.Blog, int>", generated);
    }

    [Fact]
    public void Generator_scopes_conventions_to_each_context_and_honors_foreign_key_attribute()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace First
            {
            public sealed class FirstContext : DbContext
            {
                public FirstContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class User { public int Id { get; set; } }
            public sealed class Blog { public int Id { get; set; } public int UserId { get; set; } }
            }

            namespace Second
            {
            public sealed class SecondContext : DbContext
            {
                public SecondContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class User { public int Id { get; set; } }
            public sealed class Blog { public int Id { get; set; } public int UserId { get; set; } }
            }

            namespace Explicit
            {
            public sealed class ExplicitContext : DbContext
            {
                public ExplicitContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Owner> Owners => Set<Owner>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class User { public int Id { get; set; } }
            public sealed class Owner { public int Id { get; set; } }
            public sealed class Blog
            {
                public int Id { get; set; }
                public int UserId { get; set; }
                [ForeignKey(nameof(UserId))]
                public Owner? Owner { get; set; }
            }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("RegisterGeneratedForeignKey<global::First.Blog, global::First.User>", generated);
        Assert.Contains("RegisterGeneratedForeignKey<global::Second.Blog, global::Second.User>", generated);
        Assert.DoesNotContain("RegisterGeneratedForeignKey<global::First.Blog, global::Second.User>", generated);
        Assert.Contains("RegisterGeneratedForeignKey<global::Explicit.Blog, global::Explicit.Owner>", generated);
        Assert.DoesNotContain("RegisterGeneratedForeignKey<global::Explicit.Blog, global::Explicit.User>", generated);
    }

    [Fact]
    public void Generator_does_not_globalize_relationships_for_entities_shared_by_contexts()
    {
        const string source = """
            using Perigon.PostgreSQL;
            namespace Shared;
            public sealed class FirstContext : DbContext
            {
                public FirstContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class SecondContext : DbContext
            {
                public SecondContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class User { public int Id { get; set; } }
            public sealed class Blog { public int Id { get; set; } public int UserId { get; set; } }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("RegisterGeneratedForeignKey<global::Shared.Blog, global::Shared.User>", generated);
    }

    [Fact]
    public void Generator_uses_non_nullable_principal_key_for_nullable_foreign_key_loader()
    {
        const string source = """
            using Perigon.PostgreSQL;
            namespace NullableFk;
            public sealed class DemoContext : DbContext
            {
                public DemoContext() : base(_ => { }) { }
                public DbSet<User> Users => Set<User>();
                public DbSet<Blog> Blogs => Set<Blog>();
            }
            public sealed class User { public int Id { get; set; } }
            public sealed class Blog
            {
                public int Id { get; set; }
                public int? UserId { get; set; }
                public User? User { get; set; }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("LoadReferenceAsync<global::NullableFk.Blog, global::NullableFk.User, int>", generated);
    }

    [Fact]
    public void Generator_registers_migrations_without_dbsets_in_stable_id_order()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using Perigon.PostgreSQL.Migrations;

            namespace Demo
            {
                public sealed class DemoDbContext : DbContext
                {
                    public DemoDbContext() { }
                }

                [Migration(typeof(DemoDbContext), "202610190102_AddSecond")]
                public sealed class AddSecondMigration : Migration
                {
                    public override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
                    public override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
                }

                [Migration(typeof(DemoDbContext), "202610190101_AddFirst")]
                public sealed class AddFirstMigration : Migration
                {
                    public override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
                    public override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");
                }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("MigrationRegistry.RegisterContext<global::Demo.DemoDbContext>", generated);
        Assert.Contains("new global::Perigon.PostgreSQL.Migrations.RegisteredMigration(\"202610190101_AddFirst\", new global::Demo.AddFirstMigration())", generated);
        Assert.Contains("new global::Perigon.PostgreSQL.Migrations.RegisteredMigration(\"202610190102_AddSecond\", new global::Demo.AddSecondMigration())", generated);
        Assert.DoesNotContain("EntityModelRegistry.RegisterContext<global::Demo.DemoDbContext>", generated);

        var first = generated.IndexOf("202610190101_AddFirst", StringComparison.Ordinal);
        var second = generated.IndexOf("202610190102_AddSecond", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first);
    }

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
        Assert.Contains("EntityMaterializerRegistry.RegisterFactory<global::Demo.UserProjection>", generated);
        Assert.Contains("private static global::Demo.UserProjection Materialize_", generated);
        Assert.DoesNotContain("AbstractProjection", generated);
    }

    [Fact]
    public void Generator_emits_compilable_materializer_for_init_properties_and_escapes_literals()
    {
        const string source = """
            using Perigon.PostgreSQL;
            using Perigon.PostgreSQL.Attributes;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<InitProjection> Projections => Set<InitProjection>();
            }

            [Table("demo\nusers", Schema = "demo\rschema")]
            [Comment("line\tcomment")]
            public sealed class InitProjection
            {
                public int Id { get; init; }

                public string Name { get; set; } = "default";

                public DemoStatus? Status { get; init; }
            }

            public enum DemoStatus { Unknown, Active }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("private static global::Demo.InitProjection Materialize_", generated);
        Assert.Contains("Id = ReadValue<", generated);
        Assert.Contains("reader.GetFieldValue<T>(ordinal)", generated);
        Assert.Contains("ConvertValue<T>", generated);
        Assert.Contains("\"demo\\nusers\"", generated);
        Assert.Contains("\"demo\\rschema\"", generated);
        Assert.Contains("\"line\\tcomment\"", generated);
    }

    [Fact]
    public void Generator_uses_distinct_materializer_names_for_colliding_sanitized_types()
    {
        const string source = """
            using Perigon.PostgreSQL;

            namespace Demo.A
            {
                public sealed class B
                {
                    public int Id { get; set; }
                }
            }

            namespace Demo
            {
                public sealed class A_B
                {
                    public int Id { get; set; }
                }
            }
            """;

        _ = RunGenerator(source, out var diagnostics);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generator_reports_unsupported_positional_records_without_emitting_invalid_materializer_code()
    {
        const string source = """
            using Perigon.PostgreSQL;

            namespace Demo;

            public sealed class DemoDbContext : DbContext
            {
                public DemoDbContext() : base(_ => { }) { }

                public DbSet<ImmutableUser> Users => Set<ImmutableUser>();
            }

            public sealed record ImmutableUser(int Id, string Name);
            """;

        var generated = RunGenerator(source, out var diagnostics);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Id == "PGSG001" && diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("Materialize_global__Demo_ImmutableUser", generated);
        Assert.Contains("EntityModelRegistry.Register<global::Demo.ImmutableUser>", generated);
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
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        diagnostics = outputCompilation.GetDiagnostics().AddRange(generatorDiagnostics);
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
