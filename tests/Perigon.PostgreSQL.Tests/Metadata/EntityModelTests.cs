using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Tests.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Metadata;

public sealed class EntityModelTests
{
    [Fact]
    public void Convention_model_uses_default_table_and_column_names()
    {
        var model = EntityModel.For<ConventionUser>();

        Assert.True(model.IsGenerated);
        Assert.True(EntityMaterializerRegistry.TryGet<ConventionUser>(out _));
        Assert.True(EntityValueAccessorRegistry.TryGetAccessor(typeof(ConventionUser), nameof(ConventionUser.UserName), out _));
        Assert.Equal("convention_users", model.TableName);
        Assert.Equal("id", model.GetColumn(nameof(ConventionUser.Id)).ColumnName);
        Assert.Equal("user_name", model.GetColumn(nameof(ConventionUser.UserName)).ColumnName);
        Assert.Equal("age", model.GetColumn(nameof(ConventionUser.Age)).ColumnName);
        Assert.Equal("status", model.GetColumn(nameof(ConventionUser.Status)).ColumnName);
        Assert.Equal("tags", model.GetColumn(nameof(ConventionUser.Tags)).ColumnName);
    }

    [Fact]
    public void Convention_model_treats_id_as_primary_key_and_identity()
    {
        var model = EntityModel.For<ConventionUser>();

        Assert.NotNull(model.PrimaryKey);
        Assert.Equal(nameof(ConventionUser.Id), model.PrimaryKey.PropertyName);
        Assert.True(model.PrimaryKey.IsIdentity);
    }

    [Fact]
    public void Attribute_model_uses_explicit_table_column_and_schema()
    {
        var model = EntityModel.For<AttributedUser>();

        Assert.Equal("security", model.Schema);
        Assert.Equal("app_users", model.TableName);
        Assert.Equal("\"security\".\"app_users\"", model.StoreObjectName);
        Assert.Equal("user_id", model.GetColumn(nameof(AttributedUser.Key)).ColumnName);
        Assert.Equal("display_name", model.GetColumn(nameof(AttributedUser.Name)).ColumnName);
        Assert.Equal("roles", model.GetColumn(nameof(AttributedUser.Roles)).ColumnName);
    }

    [Fact]
    public void NotMapped_properties_are_excluded()
    {
        var model = EntityModel.For<AttributedUser>();

        Assert.DoesNotContain(model.Columns, c => c.PropertyName == nameof(AttributedUser.RuntimeOnly));
    }

    [Fact]
    public void Standard_data_annotations_are_used_for_metadata()
    {
        var model = EntityModel.For<StandardAnnotatedUser>();

        Assert.Equal("identity", model.Schema);
        Assert.Equal("standard_users", model.TableName);
        Assert.Equal("user_id", model.GetColumn(nameof(StandardAnnotatedUser.Id)).ColumnName);
        Assert.Equal("text", model.GetColumn(nameof(StandardAnnotatedUser.Email)).TypeName);
        Assert.True(model.GetColumn(nameof(StandardAnnotatedUser.Id)).IsPrimaryKey);
        Assert.True(model.GetColumn(nameof(StandardAnnotatedUser.Id)).IsIdentity);
        Assert.False(model.GetColumn(nameof(StandardAnnotatedUser.Email)).IsNullable);
        Assert.Equal(200, model.GetColumn(nameof(StandardAnnotatedUser.Email)).MaxLength);
        Assert.DoesNotContain(model.Columns, c => c.PropertyName == nameof(StandardAnnotatedUser.RuntimeOnly));
    }

    [Fact]
    public void Ef_index_attribute_is_read_without_runtime_ef_dependency()
    {
        var model = EntityModel.For<IndexedUser>();

        var index = Assert.Single(model.Indexes);
        Assert.Equal("uq_indexed_users_email", index.IndexName);
        Assert.True(index.IsUnique);
        Assert.Equal("email", Assert.Single(index.Columns).ColumnName);
    }

    [Fact]
    public void NotMapped_entity_type_is_rejected_by_runtime_fallback()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EntityModel.For<RuntimeNotMappedEntity>());

        Assert.Contains("not mapped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void View_attribute_marks_model_as_view()
    {
        var model = EntityModel.For<ActiveUserView>();

        Assert.True(model.IsView);
        Assert.Equal("reporting", model.Schema);
        Assert.Equal("active_user_view", model.TableName);
    }

    [Table("standard_users", Schema = "identity")]
    private sealed class StandardAnnotatedUser
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
        public string RuntimeOnly { get; set; } = "";
    }

    [NotMapped]
    private sealed class RuntimeNotMappedEntity
    {
        public int Id { get; set; }
    }
}
