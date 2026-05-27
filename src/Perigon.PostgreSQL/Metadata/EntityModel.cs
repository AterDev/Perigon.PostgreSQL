using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Perigon.PostgreSQL.Attributes;
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
        bool isGenerated)
    {
        ClrType = clrType;
        Schema = schema;
        TableName = tableName;
        Columns = columns;
        PrimaryKey = columns.FirstOrDefault(c => c.IsPrimaryKey);
        ForeignKeys = foreignKeys;
        Indexes = CreateIndexes(indexes);
        IsView = isView;
        Comment = comment;
        IsGenerated = isGenerated;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type ClrType { get; }

    public string? Schema { get; }

    public string TableName { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public ColumnModel? PrimaryKey { get; }

    public IReadOnlyList<ForeignKeyModel> ForeignKeys { get; }

    public IReadOnlyList<IndexModel> Indexes { get; }

    public bool IsView { get; }

    public string? Comment { get; }

    public bool IsGenerated { get; }

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
        if (EntityModelRegistry.TryGet(entityType, out var generated))
        {
            return generated;
        }

        return Build(entityType);
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
        string? comment = null)
        where T : class
    {
        return new EntityModel(typeof(T), schema, tableName, columns, Array.Empty<ForeignKeyModel>(), isView, indexes, comment, isGenerated: true);
    }

    internal EntityModel Apply(
        string? schema,
        string? tableName,
        IReadOnlyList<ColumnModel> columns,
        string? comment,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyList<ForeignKeyModel>? foreignKeys)
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
            IsGenerated);
    }

    private static EntityModel Build(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        if (IsNotMapped(entityType))
        {
            throw new InvalidOperationException($"Entity type '{entityType.FullName}' is marked as not mapped.");
        }

        var perigonTable = entityType.GetCustomAttribute<TableAttribute>();
        var schemaTable = entityType.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>();
        var view = entityType.GetCustomAttribute<ViewAttribute>();
        var entityComment = ReadComment(entityType.GetCustomAttributes(inherit: false));
        var tableName = view?.Name ?? perigonTable?.Name ?? schemaTable?.Name ?? NamingConventions.DefaultTableName(entityType);
        var schema = view?.Schema ?? perigonTable?.Schema ?? schemaTable?.Schema;
        var nullability = new NullabilityInfoContext();
        var properties = entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && !IsNotMapped(p))
            .ToArray();

        var columns = new List<ColumnModel>(properties.Length);
        foreach (var property in properties)
        {
            var perigonColumn = property.GetCustomAttribute<ColumnAttribute>();
            var schemaColumn = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
            var databaseGenerated = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute>();
            var databaseGeneratedOption = databaseGenerated?.DatabaseGeneratedOption;
            var isPrimaryKey = perigonColumn?.IsPrimaryKey == true ||
                               property.GetCustomAttribute<System.ComponentModel.DataAnnotations.KeyAttribute>() is not null ||
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
                perigonColumn?.Name ?? schemaColumn?.Name ?? NamingConventions.DefaultColumnName(property.Name),
                perigonColumn?.TypeName ?? schemaColumn?.TypeName,
                isPrimaryKey,
                perigonColumn?.IsIdentity == true || databaseGeneratedOption == System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Identity || conventionIdentity,
                perigonColumn?.IsGenerated == true || databaseGeneratedOption == System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Computed,
                perigonColumn?.IsArray == true || IsArrayLike(property.PropertyType),
                IsNullable(property, nullability),
                maxLength,
                precision,
                scale,
                comment,
                isUnicode));
        }

            return new EntityModel(entityType, schema, tableName, columns, Array.Empty<ForeignKeyModel>(), view is not null, ReadIndexDefinitions(entityType), entityComment, isGenerated: false);
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
            var name = string.IsNullOrWhiteSpace(definition.Name)
                ? DefaultIndexName(columns, definition.IsUnique)
                : definition.Name!;
            indexes.Add(new IndexModel(name, this, columns, definition.IsUnique));
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
        return member.GetCustomAttribute<NotMappedAttribute>() is not null ||
               member.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() is not null;
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
