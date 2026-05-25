using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Tests.Models;

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
}
