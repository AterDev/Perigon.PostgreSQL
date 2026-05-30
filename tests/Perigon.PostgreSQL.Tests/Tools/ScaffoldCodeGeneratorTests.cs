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
                "user table comment",
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null, null, null, null, null, null),
                    new ColumnModel("email", "character varying", "varchar", false, false, false, null, 200, null, null, "email comment", null),
                    new ColumnModel("amount", "numeric", "numeric", false, false, false, "0", null, 10, 2, null, null),
                    new ColumnModel("created_at", "timestamp with time zone", "timestamptz", false, false, false, null, null, 2, null, null, null),
                    new ColumnModel("active_dates", "ARRAY", "_date", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("normalized_email", "text", "text", false, false, true, null, null, null, null, null, "lower(email)")
                ],
                ["id"],
                [],
                [new IndexModel("uq_users_email", ["email"], true)]),
            new TableModel(
                "audit",
                "users",
                false,
                null,
                [
                    new ColumnModel("id", "uuid", "uuid", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("email", "text", "text", false, false, false, null, null, null, null, null, null)
                ],
                ["id"],
                [],
                []),
            new TableModel(
                "public",
                "blogs",
                false,
                null,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null, null, null, null, null, null),
                    new ColumnModel("user_id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("name", "text", "text", false, false, false, null, null, null, null, null, null)
                ],
                ["id"],
                [new ForeignKeyModel("fk_blogs_user_id", "user_id", "public", "users", "id", "Cascade")],
                []),
            new TableModel(
                "public",
                "order_lines",
                false,
                null,
                [
                    new ColumnModel("order_id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("line_no", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("status", "text", "text", false, false, false, "'draft'", null, null, null, null, null)
                ],
                ["order_id", "line_no"],
                [],
                [new IndexModel("ix_order_lines_status", ["status"], false, ["line_no"], "status <> ''", "gin")]),
            new TableModel(
                "public",
                "shipments",
                false,
                null,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null, null, null, null, null, null),
                    new ColumnModel("order_id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("line_no", "integer", "int4", false, false, false, null, null, null, null, null, null)
                ],
                ["id"],
                [new ForeignKeyModel("fk_shipments_order_lines", ["order_id", "line_no"], "public", "order_lines", ["order_id", "line_no"], "NoAction")],
                []),
            new TableModel(
                "reporting",
                "active_users",
                true,
                null,
                [
                    new ColumnModel("id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("email", "text", "text", false, false, false, null, null, null, null, null, null)
                ],
                [],
                [],
                [])
        ]);

        var files = new ScaffoldCodeGenerator(options).Generate(database);

        AssertGeneratedCodeCompiles(files);

        var context = Assert.Single(files, file => file.RelativePath == Path.Combine("AppDbContext", "AppDbContext.cs")).Content;
        Assert.Contains("using Demo.Data.Entity;", context);
        Assert.Contains("namespace Demo.Data;", context);
        Assert.Contains("public DbSet<User> Users => Set<User>();", context);
        Assert.Contains("public DbSet<AuditUser> AuditUsers => Set<AuditUser>();", context);
        Assert.Contains("public DbSet<Blog> Blogs => Set<Blog>();", context);
        Assert.Contains("public DbSet<OrderLine> OrderLines => Set<OrderLine>();", context);
        Assert.Contains("public DbSet<Shipment> Shipments => Set<Shipment>();", context);
        Assert.Contains("public DbSet<ActiveUser> ActiveUsers => Set<ActiveUser>();", context);
        Assert.Contains("protected override void OnModelCreating(ModelBuilder modelBuilder)", context);
        Assert.Contains("entity.Property(x => x.Amount).HasDefaultSql(\"0\");", context);
        Assert.Contains("entity.Property(x => x.NormalizedEmail).HasGeneratedColumnSql(\"lower(email)\");", context);
        Assert.Contains("entity.HasOne(x => x.User)", context);
        Assert.Contains(".HasForeignKey(x => x.UserId)", context);
        Assert.Contains(".HasConstraintName(\"fk_blogs_user_id\")", context);
        Assert.Contains(".OnDelete(ReferentialAction.Cascade)", context);
        Assert.Contains("entity.HasKey(x => new { x.OrderId, x.LineNo });", context);
        Assert.Contains("entity.HasIndex(x => x.Status)", context);
        Assert.Contains(".IncludeProperties(x => x.LineNo)", context);
        Assert.Contains(".HasFilter(\"status <> ''\")", context);
        Assert.Contains(".HasMethod(\"gin\")", context);
        Assert.Contains("entity.HasOne(x => x.OrderLine)", context);
        Assert.Contains(".HasForeignKey(x => new { x.OrderId, x.LineNo })", context);

        var user = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "User.cs")).Content;
        Assert.Contains("namespace Demo.Data.Entity;", user);
        Assert.Contains("using System;", user);
        Assert.Contains("using PerigonCommentAttribute = Perigon.PostgreSQL.Attributes.CommentAttribute;", user);
        Assert.Contains("using PerigonIndexAttribute = Perigon.PostgreSQL.Attributes.IndexAttribute;", user);
        Assert.Contains("using PerigonPrecisionAttribute = Perigon.PostgreSQL.Attributes.PrecisionAttribute;", user);
        Assert.Contains("[PerigonIndexAttribute(nameof(Email), Name = \"uq_users_email\", IsUnique = true)]", user);
        Assert.Contains("[PerigonCommentAttribute(\"user table comment\")]", user);
        Assert.Contains("[Table(\"users\", Schema = \"public\")]", user);
        Assert.Contains("[Key]", user);
        Assert.Contains("[DatabaseGenerated(DatabaseGeneratedOption.Identity)]", user);
        Assert.Contains("[DatabaseGenerated(DatabaseGeneratedOption.Computed)]", user);
        Assert.Contains("[MaxLength(200)]", user);
        Assert.Contains("[PerigonCommentAttribute(\"email comment\")]", user);
        Assert.Contains("public string Email { get; set; } = \"\";", user);
        Assert.Contains("[PerigonPrecisionAttribute(10, 2)]", user);
        Assert.Contains("public decimal Amount { get; set; }", user);
        Assert.Contains("[PerigonPrecisionAttribute(2)]", user);
        Assert.Contains("public DateTimeOffset CreatedAt { get; set; }", user);
        Assert.Contains("public DateOnly[] ActiveDates { get; set; } = [];", user);
        Assert.Contains("public string NormalizedEmail { get; set; } = \"\";", user);

        var orderLine = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "OrderLine.cs")).Content;
        Assert.DoesNotContain("[Key]", orderLine);
        Assert.DoesNotContain("[PerigonIndexAttribute", orderLine);

        var shipment = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "Shipment.cs")).Content;
        Assert.DoesNotContain("[ForeignKey(nameof(OrderLine))]", shipment);
        Assert.Contains("public OrderLine? OrderLine { get; set; }", shipment);

        var auditUser = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "AuditUser.cs")).Content;
        Assert.Contains("public Guid Id { get; set; }", auditUser);

        var blog = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "Blog.cs")).Content;
        Assert.Contains("[ForeignKey(nameof(User))]", blog);
        Assert.Contains("public int UserId { get; set; }", blog);
        Assert.Contains("[NotMapped]", blog);
        Assert.Contains("public User? User { get; set; }", blog);

        var view = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "ActiveUser.cs")).Content;
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
                null,
                [new ColumnModel("id", "integer", "int4", false, true, false, null, null, null, null, null, null)],
                ["id"],
                [],
                []),
            new TableModel(
                "public",
                "sessions",
                false,
                null,
                [
                    new ColumnModel("id", "integer", "int4", false, true, false, null, null, null, null, null, null),
                    new ColumnModel("user-id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("user_id", "integer", "int4", false, false, false, null, null, null, null, null, null)
                ],
                ["id"],
                [
                    new ForeignKeyModel("fk_sessions_user_id_1", "user-id", "public", "users", "id", "NoAction"),
                    new ForeignKeyModel("fk_sessions_user_id_2", "user_id", "public", "users", "id", "NoAction")
                ],
                [])
        ]);

        var files = new ScaffoldCodeGenerator(options).Generate(database);

        AssertGeneratedCodeCompiles(files);

        var session = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "Session.cs")).Content;
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
            "--no-views"
        ]);

        Assert.False(options.IncludeViews);
        Assert.Equal("DefaultDbContext", options.ContextName);
        Assert.Equal("AppDbContext", options.Namespace);
        Assert.Equal(".", options.OutputDirectory);
        Assert.Equal(Path.Combine("AppDbContext", "DefaultDbContext.cs"), options.ContextRelativePath);
        Assert.Equal("Entity", options.EntityRelativeDirectory);
        Assert.Equal("AppDbContext.Entity", options.EntityNamespace);
    }

    [Fact]
    public void Generate_uses_fluent_key_configuration_for_composite_primary_keys()
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
                "order_lines",
                false,
                null,
                [
                    new ColumnModel("order_id", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("line_no", "integer", "int4", false, false, false, null, null, null, null, null, null),
                    new ColumnModel("name", "text", "text", false, false, false, null, null, null, null, null, null)
                ],
                ["order_id", "line_no"],
                [],
                [])
        ]);

        var files = new ScaffoldCodeGenerator(options).Generate(database);

        AssertGeneratedCodeCompiles(files);

        var entity = Assert.Single(files, file => file.RelativePath == Path.Combine("Entity", "OrderLine.cs")).Content;
        Assert.DoesNotContain("[Key]", entity);

        var context = Assert.Single(files, file => file.RelativePath == Path.Combine("AppDbContext", "AppDbContext.cs")).Content;
        Assert.Contains("entity.HasKey(x => new { x.OrderId, x.LineNo });", context);
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