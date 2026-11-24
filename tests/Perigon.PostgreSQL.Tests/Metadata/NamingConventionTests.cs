using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Tests.Models;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Metadata;

public sealed class NamingConventionTests
{
    [Theory]
    [InlineData(typeof(ArticleCategory), "ArticleCategories", "article_categories")]
    [InlineData(typeof(Person), "People", "people")]
    [InlineData(typeof(News), "News", "news")]
    public void Default_table_names_use_english_pluralization(Type entityType, string pascalCase, string snakeCase)
    {
        var tableName = NamingConventions.DefaultTableName(entityType);

        Assert.Equal(pascalCase, tableName);
        Assert.Equal(snakeCase, NamingConventions.ToSnakeCase(tableName));
    }

    private sealed class ArticleCategory;
    private sealed class Person;
    private sealed class News;

    [Theory]
    [InlineData("UserName", "user_name")]
    [InlineData("URLValue", "url_value")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("CreatedAtUtc", "created_at_utc")]
    public void ToSnakeCase_handles_common_dotnet_names(string input, string expected)
    {
        Assert.Equal(expected, NamingConventions.ToSnakeCase(input));
    }

    [Fact]
    public void Quote_escapes_embedded_quotes()
    {
        Assert.Equal("\"a\"\"b\"", Identifier.Quote("a\"b"));
    }

    [Fact]
    public void Qualify_quotes_schema_and_table()
    {
        Assert.Equal("\"audit\".\"users\"", Identifier.Qualify("audit", "users"));
    }

    [Fact]
    public void Explicit_pascal_convention_overrides_assembly_snake_case_opt_in()
    {
        using var context = new PascalNamingContext();
        var model = Assert.Single(context.ReadModels());

        Assert.Equal("ConventionUsers", model.TableName);
        Assert.Equal("UserName", model.GetColumn(nameof(ConventionUser.UserName)).ColumnName);
    }

    [Fact]
    public void Explicit_snake_convention_is_applied_to_generated_models()
    {
        using var context = new SnakeNamingContext();
        var model = Assert.Single(context.ReadModels());

        Assert.Equal("convention_users", model.TableName);
        Assert.Equal("user_name", model.GetColumn(nameof(ConventionUser.UserName)).ColumnName);
    }

    [Fact]
    public void Explicit_names_that_match_conventions_are_preserved()
    {
        using var context = new ExplicitNamesContext();
        var model = Assert.Single(context.ReadModels());

        Assert.Equal("ExplicitNamesEntities", model.TableName);
        Assert.Equal("UserName", model.GetColumn(nameof(ExplicitNamesEntity.UserName)).ColumnName);
    }
}

public sealed class PascalNamingContext : DbContext
{
    public PascalNamingContext()
        : base(builder => builder.UseNamingConvention(NamingConvention.PascalCase))
    {
    }

    public DbSet<ConventionUser> Users => Set<ConventionUser>();

    public IReadOnlyList<EntityModel> ReadModels() => GetEntityModels();
}

public sealed class SnakeNamingContext : DbContext
{
    public SnakeNamingContext()
        : base(builder => builder.UseNamingConvention(NamingConvention.SnakeCase))
    {
    }

    public DbSet<ConventionUser> Users => Set<ConventionUser>();

    public IReadOnlyList<EntityModel> ReadModels() => GetEntityModels();
}

[Table("ExplicitNamesEntities")]
public sealed class ExplicitNamesEntity
{
    public int Id { get; set; }

    [Column("UserName")]
    public string UserName { get; set; } = "";
}

public sealed class ExplicitNamesContext : DbContext
{
    public ExplicitNamesContext()
        : base(builder => builder.UseNamingConvention(NamingConvention.SnakeCase))
    {
    }

    public DbSet<ExplicitNamesEntity> Entities => Set<ExplicitNamesEntity>();

    public IReadOnlyList<EntityModel> ReadModels() => GetEntityModels();
}
