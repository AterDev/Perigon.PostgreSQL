using System.Linq.Expressions;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL;

public sealed class ModelBuilder
{
    private readonly Dictionary<Type, EntityConfiguration> _configurations = new();

    public EntityTypeBuilder<TEntity> Entity<TEntity>()
        where TEntity : class
    {
        return new EntityTypeBuilder<TEntity>(GetOrAddConfiguration(typeof(TEntity)));
    }

    public EntityTypeBuilder<TEntity> Entity<TEntity>(Action<EntityTypeBuilder<TEntity>> configure)
        where TEntity : class
    {
        var builder = Entity<TEntity>();
        configure(builder);
        return builder;
    }

    internal IReadOnlyList<EntityModel> Apply(IReadOnlyList<EntityModel> models)
    {
        if (_configurations.Count == 0)
        {
            return models;
        }

        var configured = new List<EntityModel>(models.Count);
        foreach (var model in models)
        {
            if (!_configurations.TryGetValue(model.ClrType, out var configuration))
            {
                configured.Add(model);
                continue;
            }

            var columns = model.Columns
                .Select(column => configuration.Properties.TryGetValue(column.PropertyName, out var property)
                    ? column.Apply(property)
                    : column)
                .ToArray();

            var indexes = MergeIndexes(model, configuration);
            configured.Add(model.Apply(configuration.Schema, configuration.TableName, columns, configuration.Comment, indexes, foreignKeys: null));
        }

        if (_configurations.Values.All(static configuration => configuration.Relationships.Count == 0))
        {
            return configured;
        }

        var configuredByType = configured.ToDictionary(model => model.ClrType, model => model);
        var explicitForeignKeys = new Dictionary<Type, List<ForeignKeyModel>>();
        foreach (var configuration in _configurations.Values)
        {
            foreach (var relationship in configuration.Relationships)
            {
                if (!configuredByType.TryGetValue(configuration.EntityType, out var dependent) ||
                    !configuredByType.TryGetValue(relationship.PrincipalType, out var principal))
                {
                    throw new InvalidOperationException($"Fluent relationship between '{configuration.EntityType.FullName}' and '{relationship.PrincipalType.FullName}' could not be resolved from the registered DbContext models.");
                }

                var dependentColumn = dependent.GetColumn(relationship.DependentPropertyName ?? throw new InvalidOperationException(
                    $"Fluent relationship on '{configuration.EntityType.FullName}' is missing HasForeignKey(...)."));
                if (principal.PrimaryKey is null)
                {
                    throw new InvalidOperationException($"Principal entity '{principal.ClrType.FullName}' does not have a primary key for the configured fluent relationship.");
                }

                if (!explicitForeignKeys.TryGetValue(configuration.EntityType, out var foreignKeys))
                {
                    foreignKeys = [];
                    explicitForeignKeys[configuration.EntityType] = foreignKeys;
                }

                foreignKeys.Add(new ForeignKeyModel(
                    relationship.ConstraintName ?? DefaultForeignKeyName(dependent, dependentColumn),
                    dependent,
                    dependentColumn,
                    principal,
                    principal.PrimaryKey,
                    relationship.OnDelete));
            }
        }

        return configured
            .Select(model => explicitForeignKeys.TryGetValue(model.ClrType, out var foreignKeys)
                ? model.Apply(model.Schema, model.TableName, model.Columns, model.Comment, model.Indexes.Select(static index =>
                    new IndexDefinition(index.IndexName, index.Columns.Select(column => column.PropertyName).ToArray(), index.IsUnique)).ToArray(), foreignKeys)
                : model)
            .ToArray();
    }

    private EntityConfiguration GetOrAddConfiguration(Type entityType)
    {
        if (_configurations.TryGetValue(entityType, out var configuration))
        {
            return configuration;
        }

        configuration = new EntityConfiguration(entityType);
        _configurations[entityType] = configuration;
        return configuration;
    }

    private static IReadOnlyList<IndexDefinition> MergeIndexes(EntityModel model, EntityConfiguration configuration)
    {
        var merged = new Dictionary<string, IndexDefinition>(StringComparer.Ordinal);

        foreach (var index in model.Indexes)
        {
            var propertyNames = index.Columns.Select(column => column.PropertyName).ToArray();
            merged[GetIndexKey(propertyNames)] = new IndexDefinition(index.IndexName, propertyNames, index.IsUnique);
        }

        foreach (var index in configuration.Indexes)
        {
            merged[GetIndexKey(index.PropertyNames)] = new IndexDefinition(index.DatabaseName, index.PropertyNames, index.IsUnique);
        }

        return merged.Values.ToArray();
    }

    private static string GetIndexKey(IReadOnlyList<string> propertyNames)
    {
        return string.Join("|", propertyNames);
    }

    private static string DefaultForeignKeyName(EntityModel dependent, ColumnModel column)
    {
        return "fk_" + dependent.TableName + "_" + column.ColumnName;
    }
}

public sealed class EntityTypeBuilder<TEntity>
    where TEntity : class
{
    private readonly EntityConfiguration _configuration;

    internal EntityTypeBuilder(EntityConfiguration configuration)
    {
        _configuration = configuration;
    }

    public EntityTypeBuilder<TEntity> ToTable(string name, string? schema = null)
    {
        _configuration.TableName = name;
        _configuration.Schema = schema;
        return this;
    }

    public EntityTypeBuilder<TEntity> HasComment(string comment)
    {
        _configuration.Comment = comment;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> Property<TProperty>(Expression<Func<TEntity, TProperty>> expression)
    {
        var propertyName = ExpressionHelpers.ReadSingleProperty(expression);
        return new PropertyBuilder<TEntity, TProperty>(_configuration.GetOrAddProperty(propertyName));
    }

    public IndexBuilder<TEntity> HasIndex(Expression<Func<TEntity, object?>> expression)
    {
        return new IndexBuilder<TEntity>(_configuration.AddIndex(ExpressionHelpers.ReadPropertyList(expression)));
    }

    public ReferenceNavigationBuilder<TEntity, TRelated> HasOne<TRelated>(Expression<Func<TEntity, TRelated?>> expression)
        where TRelated : class
    {
        return new ReferenceNavigationBuilder<TEntity, TRelated>(_configuration.AddRelationship(typeof(TRelated), ExpressionHelpers.TryReadSingleProperty(expression)));
    }

    public ReferenceNavigationBuilder<TEntity, TRelated> HasOne<TRelated>()
        where TRelated : class
    {
        return new ReferenceNavigationBuilder<TEntity, TRelated>(_configuration.AddRelationship(typeof(TRelated), navigationName: null));
    }
}

public sealed class PropertyBuilder<TEntity, TProperty>
    where TEntity : class
{
    private readonly PropertyConfiguration _configuration;

    internal PropertyBuilder(PropertyConfiguration configuration)
    {
        _configuration = configuration;
    }

    public PropertyBuilder<TEntity, TProperty> HasColumnName(string columnName)
    {
        _configuration.ColumnName = columnName;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> HasColumnType(string typeName)
    {
        _configuration.TypeName = typeName;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> IsRequired()
    {
        _configuration.IsRequired = true;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> HasMaxLength(int maxLength)
    {
        _configuration.MaxLength = maxLength;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> HasPrecision(int precision, int? scale = null)
    {
        _configuration.Precision = precision;
        _configuration.Scale = scale;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> HasComment(string comment)
    {
        _configuration.Comment = comment;
        return this;
    }

    public PropertyBuilder<TEntity, TProperty> IsUnicode(bool isUnicode = true)
    {
        _configuration.IsUnicode = isUnicode;
        return this;
    }
}

public sealed class IndexBuilder<TEntity>
    where TEntity : class
{
    private readonly IndexConfiguration _configuration;

    internal IndexBuilder(IndexConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IndexBuilder<TEntity> IsUnique(bool isUnique = true)
    {
        _configuration.IsUnique = isUnique;
        return this;
    }

    public IndexBuilder<TEntity> HasDatabaseName(string name)
    {
        _configuration.DatabaseName = name;
        return this;
    }
}

public sealed class ReferenceNavigationBuilder<TEntity, TRelated>
    where TEntity : class
    where TRelated : class
{
    private readonly RelationshipConfiguration _configuration;

    internal ReferenceNavigationBuilder(RelationshipConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ReferenceCollectionBuilder<TEntity, TRelated> WithMany(Expression<Func<TRelated, IEnumerable<TEntity>>> expression)
    {
        _configuration.PrincipalNavigationName = ExpressionHelpers.TryReadSingleProperty(expression);
        return new ReferenceCollectionBuilder<TEntity, TRelated>(_configuration);
    }

    public ReferenceCollectionBuilder<TEntity, TRelated> WithMany()
    {
        return new ReferenceCollectionBuilder<TEntity, TRelated>(_configuration);
    }
}

public sealed class ReferenceCollectionBuilder<TEntity, TRelated>
    where TEntity : class
    where TRelated : class
{
    private readonly RelationshipConfiguration _configuration;

    internal ReferenceCollectionBuilder(RelationshipConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ReferenceCollectionBuilder<TEntity, TRelated> HasForeignKey(Expression<Func<TEntity, object?>> expression)
    {
        _configuration.DependentPropertyName = ExpressionHelpers.ReadSingleProperty(expression);
        return this;
    }

    public ReferenceCollectionBuilder<TEntity, TRelated> HasConstraintName(string name)
    {
        _configuration.ConstraintName = name;
        return this;
    }

    public ReferenceCollectionBuilder<TEntity, TRelated> OnDelete(ReferentialAction onDelete)
    {
        _configuration.OnDelete = onDelete;
        return this;
    }
}

internal sealed class EntityConfiguration
{
    public EntityConfiguration(Type entityType)
    {
        EntityType = entityType;
    }

    public Type EntityType { get; }

    public string? TableName { get; set; }

    public string? Schema { get; set; }

    public string? Comment { get; set; }

    public Dictionary<string, PropertyConfiguration> Properties { get; } = new(StringComparer.Ordinal);

    public List<IndexConfiguration> Indexes { get; } = [];

    public List<RelationshipConfiguration> Relationships { get; } = [];

    public PropertyConfiguration GetOrAddProperty(string propertyName)
    {
        if (Properties.TryGetValue(propertyName, out var configuration))
        {
            return configuration;
        }

        configuration = new PropertyConfiguration(propertyName);
        Properties[propertyName] = configuration;
        return configuration;
    }

    public IndexConfiguration AddIndex(IReadOnlyList<string> propertyNames)
    {
        var configuration = new IndexConfiguration(propertyNames);
        Indexes.Add(configuration);
        return configuration;
    }

    public RelationshipConfiguration AddRelationship(Type principalType, string? navigationName)
    {
        var configuration = new RelationshipConfiguration(principalType)
        {
            DependentNavigationName = navigationName
        };
        Relationships.Add(configuration);
        return configuration;
    }
}

internal sealed class PropertyConfiguration
{
    public PropertyConfiguration(string propertyName)
    {
        PropertyName = propertyName;
    }

    public string PropertyName { get; }

    public string? ColumnName { get; set; }

    public string? TypeName { get; set; }

    public bool? IsRequired { get; set; }

    public int? MaxLength { get; set; }

    public int? Precision { get; set; }

    public int? Scale { get; set; }

    public string? Comment { get; set; }

    public bool? IsUnicode { get; set; }
}

internal sealed class IndexConfiguration
{
    public IndexConfiguration(IReadOnlyList<string> propertyNames)
    {
        PropertyNames = propertyNames;
    }

    public IReadOnlyList<string> PropertyNames { get; }

    public string? DatabaseName { get; set; }

    public bool IsUnique { get; set; }
}

internal sealed class RelationshipConfiguration
{
    public RelationshipConfiguration(Type principalType)
    {
        PrincipalType = principalType;
    }

    public Type PrincipalType { get; }

    public string? DependentNavigationName { get; set; }

    public string? PrincipalNavigationName { get; set; }

    public string? DependentPropertyName { get; set; }

    public string? ConstraintName { get; set; }

    public ReferentialAction OnDelete { get; set; }
}

internal static class ExpressionHelpers
{
    public static string ReadSingleProperty(LambdaExpression expression)
    {
        return UnwrapToMember(expression.Body).Member.Name;
    }

    public static string? TryReadSingleProperty(LambdaExpression? expression)
    {
        return expression is null ? null : ReadSingleProperty(expression);
    }

    public static IReadOnlyList<string> ReadPropertyList(LambdaExpression expression)
    {
        var body = UnwrapConvert(expression.Body);
        if (body is NewExpression tuple)
        {
            return tuple.Arguments.Select(argument => UnwrapToMember(argument).Member.Name).ToArray();
        }

        return [UnwrapToMember(body).Member.Name];
    }

    private static MemberExpression UnwrapToMember(Expression expression)
    {
        var current = UnwrapConvert(expression);
        return current as MemberExpression
            ?? throw new NotSupportedException("Only simple property member expressions are supported by the current Perigon fluent API.");
    }

    private static Expression UnwrapConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        return current;
    }
}