using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tools.CodeGeneration;
using Perigon.PostgreSQL.Tools.ReverseEngineering;

namespace Perigon.PostgreSQL.Tests.Tools;

public sealed class ScaffoldCodeGeneratorTests
{
    [Fact]
    public void Generate_outputs_context_entities_foreign_keys_and_views()
    {
        var options = new ScaffoldOptions
        {
            ConnectionString = "Host=localhost;Database=demo",
            ContextName = "AppDbContext",
            Namespace = "Demo.Data",
            OutputDirectory = "Generated"
        };
        var database = new DatabaseModel(
        [
            new TableModel(
                "public",
                "users",
                false,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null),
                    new ColumnModel("email", "text", "text", false, false, false, null),
                    new ColumnModel("normalized_email", "text", "text", false, false, true, null)
                ],
                ["id"],
                [],
                [new IndexModel("uq_users_email", ["email"], true)]),
            new TableModel(
                "audit",
                "users",
                false,
                [
                    new ColumnModel("id", "uuid", "uuid", false, false, false, null),
                    new ColumnModel("email", "text", "text", false, false, false, null)
                ],
                ["id"],
                [],
                []),
            new TableModel(
                "public",
                "blogs",
                false,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null),
                    new ColumnModel("user_id", "integer", "int4", false, false, false, null),
                    new ColumnModel("name", "text", "text", false, false, false, null)
                ],
                ["id"],
                [new ForeignKeyModel("user_id", "public", "users", "id")],
                []),
            new TableModel(
                "reporting",
                "active_users",
                true,
                [
                    new ColumnModel("id", "integer", "int4", false, false, false, null),
                    new ColumnModel("email", "text", "text", false, false, false, null)
                ],
                [],
                [],
                [])
        ]);

        var files = new ScaffoldCodeGenerator(options).Generate(database);

        AssertGeneratedCodeCompiles(files);

        var context = Assert.Single(files, file => file.RelativePath == "AppDbContext.cs").Content;
        Assert.Contains("public DbSet<User> Users => Set<User>();", context);
        Assert.Contains("public DbSet<AuditUser> AuditUsers => Set<AuditUser>();", context);
        Assert.Contains("public DbSet<Blog> Blogs => Set<Blog>();", context);
        Assert.Contains("public DbSet<ActiveUser> ActiveUsers => Set<ActiveUser>();", context);

        var user = Assert.Single(files, file => file.RelativePath == "User.cs").Content;
        Assert.Contains("using System;", user);
        Assert.Contains("using PerigonIndexAttribute = Perigon.PostgreSQL.Attributes.IndexAttribute;", user);
        Assert.Contains("[PerigonIndexAttribute(nameof(Email), Name = \"uq_users_email\", IsUnique = true)]", user);
        Assert.Contains("[Table(\"users\", Schema = \"public\")]", user);
        Assert.Contains("[Key]", user);
        Assert.Contains("[DatabaseGenerated(DatabaseGeneratedOption.Identity)]", user);
        Assert.Contains("[DatabaseGenerated(DatabaseGeneratedOption.Computed)]", user);
        Assert.Contains("public string Email { get; set; } = \"\";", user);
        Assert.Contains("public string NormalizedEmail { get; set; } = \"\";", user);

        var auditUser = Assert.Single(files, file => file.RelativePath == "AuditUser.cs").Content;
        Assert.Contains("public Guid Id { get; set; }", auditUser);

        var blog = Assert.Single(files, file => file.RelativePath == "Blog.cs").Content;
        Assert.Contains("[ForeignKey(nameof(User))]", blog);
        Assert.Contains("public int UserId { get; set; }", blog);
        Assert.Contains("[NotMapped]", blog);
        Assert.Contains("public User? User { get; set; }", blog);

        var view = Assert.Single(files, file => file.RelativePath == "ActiveUser.cs").Content;
        Assert.Contains("[PerigonViewAttribute(\"active_users\", Schema = \"reporting\")]", view);
        Assert.Contains("using PerigonViewAttribute = Perigon.PostgreSQL.Attributes.ViewAttribute;", view);
    }

    [Fact]
    public void Generate_disambiguates_duplicate_property_names_and_navigation_names()
    {
        var options = new ScaffoldOptions
        {
            ConnectionString = "Host=localhost;Database=demo",
            ContextName = "AppDbContext",
            Namespace = "Demo.Data",
            OutputDirectory = "Generated"
        };
        var database = new DatabaseModel(
        [
            new TableModel(
                "public",
                "users",
                false,
                [new ColumnModel("id", "integer", "int4", false, true, false, null)],
                ["id"],
                [],
                []),
            new TableModel(
                "public",
                "sessions",
                false,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null),
                    new ColumnModel("user-id", "integer", "int4", false, false, false, null),
                    new ColumnModel("user_id", "integer", "int4", false, false, false, null)
                ],
                ["id"],
                [
                    new ForeignKeyModel("user-id", "public", "users", "id"),
                    new ForeignKeyModel("user_id", "public", "users", "id")
                ],
                [])
        ]);

        var files = new ScaffoldCodeGenerator(options).Generate(database);

        AssertGeneratedCodeCompiles(files);

        var session = Assert.Single(files, file => file.RelativePath == "Session.cs").Content;
        Assert.Contains("public int UserId { get; set; }", session);
        Assert.Contains("public int UserId2 { get; set; }", session);
        Assert.Contains("public User? UserUserId { get; set; }", session);
        Assert.Contains("public User? UserUserId2 { get; set; }", session);
    }

    [Fact]
    public void Parse_supports_no_views_flag()
    {
        var options = ScaffoldOptions.Parse(
        [
            "--connection", "Host=localhost;Database=demo",
            "--context", "AppDbContext",
            "--namespace", "Demo.Data",
            "--output", "Generated",
            "--no-views"
        ]);

        Assert.False(options.IncludeViews);
    }

    private static void AssertGeneratedCodeCompiles(IReadOnlyList<GeneratedFile> files)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTrees = files.Select(file => CSharpSyntaxTree.ParseText(file.Content, parseOptions, file.RelativePath)).ToArray();
        var compilation = CSharpCompilation.Create(
            "ScaffoldOutput",
            syntaxTrees,
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Empty(errors);
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