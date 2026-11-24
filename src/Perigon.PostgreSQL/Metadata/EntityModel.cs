using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Metadata;

public sealed class EntityModel
{
    private EntityModel(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type clrType,
        string? schema,
        string tableName,
        IReadOnlyList<ColumnModel> columns,
        IReadOnlyList<ForeignKeyModel> foreignKeys,
        bool isView,
        IReadOnlyList<IndexDefinition> indexes,
        string? comment,
        bool isGenerated,
        bool hasExplicitTableName)
    {
        ClrType = clrType;
        Schema = schema;
        TableName = tableName;
        Columns = columns;
        PrimaryKeys = columns.Where(static c => c.IsPrimaryKey).ToArray();
        PrimaryKey = PrimaryKeys.Count == 1 ? PrimaryKeys[0] : null;
        ForeignKeys = foreignKeys;
        Indexes = CreateIndexes(indexes);
        IsView = isView;
        Comment = comment;
        IsGenerated = isGenerated;
        HasExplicitTableName = hasExplicitTableName;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type ClrType { get; }

    public string? Schema { get; }

    public string TableName { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public IReadOnlyList<ColumnModel> PrimaryKeys { get; }

    public ColumnModel? PrimaryKey { get; }

    public IReadOnlyList<ForeignKeyModel> ForeignKeys { get; }

    public IReadOnlyList<IndexModel> Indexes { get; }

    public bool IsView { get; }

    public string? Comment { get; }

    public bool IsGenerated { get; }

    internal bool HasExplicitTableName { get; }

    public string StoreObjectName => Identifier.Qualify(Schema, TableName);

    public ColumnModel GetColumn(string propertyName)
    {
        return Columns.FirstOrDefault(c => c.PropertyName == propertyName)
            ?? throw new InvalidOperationException($"Property '{ClrType.Name}.{propertyName}' is not mapped.");
    }

    public static EntityModel For<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
        where T : class
    {
        return For(typeof(T));
    }

    public static EntityModel For(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        if (entityType.IsAbstract)
            throw new InvalidOperationException($"Abstract type '{entityType.FullName}' cannot be mapped as an entity.");

        if (EntityModelRegistry.TryGet(entityType, out var generated))
        {
            return ApplyAssemblyNamingConvention(generated, entityType);
        }

        return ApplyAssemblyNamingConvention(Build(entityType, EntityModelRegistry.GetRegisteredEntityTypes()), entityType);
    }

    private static EntityModel ApplyAssemblyNamingConvention(EntityModel model, Type entityType)
    {
        return entityType.Assembly.GetCustomAttributes(typeof(UseSnakeCaseNamingAttribute), inherit: false).Length > 0
            ? model.ApplyNamingConvention(NamingConvention.SnakeCase)
            : model;
    }

    public static EntityModel CreateGenerated<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string? schema, string tableName, IReadOnlyList<ColumnModel> columns)
        where T : class
    {
        return CreateGenerated<T>(schema, tableName, columns, isView: false, Array.Empty<IndexDefinition>(), comment: null);
    }

    public static EntityModel CreateGenerated<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string? schema,
        string tableName,
        IReadOnlyList<ColumnModel> columns,
        bool isView,
        IReadOnlyList<IndexDefinition> indexes,
        string? comment = null,
        bool hasExplicitTableName = false)
        where T : class
    {
        return new EntityModel(typeof(T), schema, tableName, columns, Array.Empty<ForeignKeyModel>(), isView, indexes, comment, isGenerated: true, hasExplicitTableName);
    }

    internal EntityModel Apply(
        string? schema,
        string? tableName,
        IReadOnlyList<ColumnModel> columns,
        string? comment,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyList<ForeignKeyModel>? foreignKeys,
        bool? hasExplicitTableName = null)
    {
        return new EntityModel(
            ClrType,
            schema ?? Schema,
            tableName ?? TableName,
            columns,
            foreignKeys ?? ForeignKeys,
            IsView,
            indexes,
            comment ?? Comment,
            IsGenerated,
            hasExplicitTableName ?? HasExplicitTableName);
    }

    internal EntityModel ApplyNamingConvention(NamingConvention namingConvention)
    {
        var tableName = !HasExplicitTableName
            ? namingConvention == NamingConvention.SnakeCase
                ? NamingConventions.ToSnakeCase(NamingConventions.DefaultTableName(ClrType))
                : NamingConventions.DefaultTableName(ClrType)
            : TableName;
        var columns = Columns
            .Select(column => !column.HasExplicitName
                ? column.WithColumnName(namingConvention == NamingConvention.SnakeCase
                    ? NamingConventions.ToSnakeCase(NamingConventions.DefaultColumnName(column.PropertyName))
                    : NamingConventions.DefaultColumnName(column.PropertyName))
                : column)
            .ToArray();
        if (tableName == TableName && Columns.SequenceEqual(columns))
        {
            return this;
        }

        var indexes = Indexes.Select(static index => new IndexDefinition(
            index.ConfiguredName,
            index.Columns.Select(static column => column.PropertyName).ToArray(),
            index.IsUnique,
            index.IncludeColumns.Select(static column => column.PropertyName).ToArray(),
            index.Filter,
            index.Method)).ToArray();

        return Apply(Schema, tableName, columns, Comment, indexes, ForeignKeys, hasExplicitTableName: HasExplicitTableName);
    }

    private static EntityModel Build(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType,
        IReadOnlySet<Type> registeredEntityTypes)
    {
        if (IsNotMapped(entityType))
        {
            throw new InvalidOperationException($"Entity type '{entityType.FullName}' is marked as not mapped.");
        }

        var table = entityType.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>();
        var view = entityType.GetCustomAttribute<ViewAttribute>();
        var entityComment = ReadComment(entityType.GetCustomAttributes(inherit: false));
        var tableName = view?.Name ?? table?.Name ?? NamingConventions.DefaultTableName(entityType);
        var schema = view?.Schema ?? table?.Schema;
        var nullability = new NullabilityInfoContext();
        var properties = entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && !IsNotMapped(p) && !IsNavigation(p, registeredEntityTypes))
            .ToArray();

        var columns = new List<ColumnModel>(properties.Length);
        foreach (var property in properties)
        {
            var column = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
            var databaseGenerated = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute>();
            var databaseGeneratedOption = databaseGenerated?.DatabaseGeneratedOption;
            var isPrimaryKey = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.KeyAttribute>() is not null ||
                               property.Name.Equals("Id", StringComparison.Ordinal) ||
                               property.Name.Equals(entityType.Name + "Id", StringComparison.Ordinal);
            var conventionIdentity = databaseGeneratedOption != System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None &&
                                     isPrimaryKey &&
                                     IsInteger(property.PropertyType);
            var maxLength = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()?.Length;
            if (maxLength is null or < 0)
            {
                maxLength = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.StringLengthAttribute>()?.MaximumLength;
            }

            var precision = ReadPrecision(property.GetCustomAttributes(inherit: false), out var scale);
            var comment = ReadComment(property.GetCustomAttributes(inherit: false));
            var isUnicode = ReadUnicode(property.GetCustomAttributes(inherit: false));

            columns.Add(new ColumnModel(
                property,
                column?.Name ?? NamingConventions.DefaultColumnName(property.Name),
                column?.TypeName,
                isPrimaryKey,
                databaseGeneratedOption == System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Identity || conventionIdentity,
                databaseGeneratedOption == System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Computed,
                IsArrayLike(property.PropertyType),
                IsNullable(property, nullability),
                maxLength,
                precision,
                scale,
                comment,
                isUnicode,
                hasExplicitName: column?.Name is not null).WithTimestampPolicy(
                    property.GetCustomAttribute<AutoTimestampAttribute>(inherit: true)?.OnInsert ?? false,
                    property.GetCustomAttribute<AutoTimestampAttribute>(inherit: true)?.OnUpdate ?? false));
        }

            return new EntityModel(entityType, schema, tableName, columns, Array.Empty<ForeignKeyModel>(), view is not null, ReadIndexDefinitions(entityType), entityComment, isGenerated: false, hasExplicitTableName: view is not null || table is not null);
    }

    private IReadOnlyList<IndexModel> CreateIndexes(IReadOnlyList<IndexDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            return Array.Empty<IndexModel>();
        }

        var indexes = new List<IndexModel>(definitions.Count);
        foreach (var definition in definitions)
        {
            var columns = definition.PropertyNames.Select(GetColumn).ToArray();
            var includeColumns = definition.IncludePropertyNames.Select(GetColumn).ToArray();
            var name = string.IsNullOrWhiteSpace(definition.Name)
                ? DefaultIndexName(columns, definition.IsUnique)
                : definition.Name!;
            indexes.Add(new IndexModel(name, this, columns, definition.IsUnique, includeColumns, definition.Filter, definition.Method)
            {
                ConfiguredName = definition.Name
            });
        }

        return indexes;
    }

    private string DefaultIndexName(IReadOnlyList<ColumnModel> columns, bool isUnique)
    {
        var prefix = isUnique ? "uq_" : "ix_";
        return prefix + TableName + "_" + string.Join("_", columns.Select(column => column.ColumnName));
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "This reflection path only reads optional index metadata attributes in the non-generated metadata fallback. Source-generated models provide index definitions without this reflection path.")]
    private static IReadOnlyList<IndexDefinition> ReadIndexDefinitions(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        var definitions = new List<IndexDefinition>();
        foreach (var attribute in entityType.GetCustomAttributes(inherit: false))
        {
            var type = attribute.GetType();
            if (!string.Equals(type.FullName, "Perigon.PostgreSQL.Attributes.IndexAttribute", StringComparison.Ordinal) &&
                !string.Equals(type.FullName, "Microsoft.EntityFrameworkCore.IndexAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            var propertyNames = type.GetProperty("PropertyNames")?.GetValue(attribute) as IReadOnlyList<string>;
            if (propertyNames is null || propertyNames.Count == 0)
            {
                continue;
            }

            var name = type.GetProperty("Name")?.GetValue(attribute) as string;
            var isUnique = type.GetProperty("IsUnique")?.GetValue(attribute) as bool? == true;
            definitions.Add(new IndexDefinition(name, propertyNames, isUnique));
        }

        return definitions;
    }

    private static bool IsInteger(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual == typeof(short) || actual == typeof(int) || actual == typeof(long);
    }

    private static bool IsArrayLike(Type type)
    {
        return type.IsArray && type != typeof(byte[]) ||
               (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "This reflection path only reads optional precision attributes in the non-generated metadata fallback.")]
    private static int? ReadPrecision(object[] attributes, out int? scale)
    {
        scale = null;
        var attribute = attributes.FirstOrDefault(static item =>
        {
            var fullName = item.GetType().FullName;
            return fullName == "Perigon.PostgreSQL.Attributes.PrecisionAttribute" ||
                   fullName == "Microsoft.EntityFrameworkCore.PrecisionAttribute";
        });

        if (attribute is null)
        {
            return null;
        }

        var type = attribute.GetType();
        var precision = type.GetProperty("Precision")?.GetValue(attribute) as int?;
        scale = type.GetProperty("Scale")?.GetValue(attribute) as int?;
        return precision;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "This reflection path only reads optional comment attributes in the non-generated metadata fallback.")]
    private static string? ReadComment(object[] attributes)
    {
        var attribute = attributes.FirstOrDefault(static item =>
        {
            var fullName = item.GetType().FullName;
            return fullName == "Perigon.PostgreSQL.Attributes.CommentAttribute" ||
                   fullName == "Microsoft.EntityFrameworkCore.CommentAttribute";
        });

        if (attribute is null)
        {
            return null;
        }

        var type = attribute.GetType();
        return type.GetProperty("Text")?.GetValue(attribute) as string ??
               type.GetProperty("Comment")?.GetValue(attribute) as string;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "This reflection path only reads optional unicode attributes in the non-generated metadata fallback.")]
    private static bool? ReadUnicode(object[] attributes)
    {
        var attribute = attributes.FirstOrDefault(static item =>
        {
            var fullName = item.GetType().FullName;
            return fullName == "Perigon.PostgreSQL.Attributes.UnicodeAttribute" ||
                   fullName == "Microsoft.EntityFrameworkCore.UnicodeAttribute";
        });

        if (attribute is null)
        {
            return null;
        }

        return attribute.GetType().GetProperty("IsUnicode")?.GetValue(attribute) as bool?;
    }

    private static bool IsNotMapped(MemberInfo member)
    {
        return member.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() is not null;
    }

    private static bool IsNavigation(PropertyInfo property, IReadOnlySet<Type> registeredEntityTypes)
    {
        if (HasExplicitColumnMapping(property))
        {
            return false;
        }

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (registeredEntityTypes.Contains(propertyType))
        {
            return true;
        }

        var elementType = GetCollectionElementType(property.PropertyType);
        if (elementType is not null && registeredEntityTypes.Contains(elementType))
        {
            return true;
        }

        // A ForeignKey attribute on a reference or collection property is the
        // reflection fallback's explicit navigation marker. On a scalar FK
        // property it points at the navigation and must remain a column.
        return property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute>() is not null &&
               !IsScalarProperty(property.PropertyType);
    }

    private static bool HasExplicitColumnMapping(PropertyInfo property)
    {
        return property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>() is not null;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The reflection fallback inspects collection interfaces to identify navigation properties. Source-generated models do not use this path.")]
    private static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var sequence = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(static item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return sequence?.GetGenericArguments()[0];
    }

    private static bool IsScalarProperty(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual.IsEnum || actual.IsPrimitive || actual == typeof(string) || actual == typeof(decimal) ||
               actual == typeof(Guid) || actual == typeof(DateTime) || actual == typeof(DateTimeOffset) ||
               actual == typeof(TimeSpan) || actual == typeof(byte[]);
    }

    private static bool IsNullable(PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() is not null)
        {
            return false;
        }

        if (property.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(property.PropertyType) is not null;
        }

        return nullability.Create(property).WriteState == NullabilityState.Nullable;
    }
}
