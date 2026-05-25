using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Perigon.PostgreSQL.Metadata;

public sealed class ColumnModel
{
    private PropertyInfo? _property;

    public ColumnModel(
        PropertyInfo property,
        string columnName,
        string? typeName,
        bool isPrimaryKey,
        bool isIdentity,
        bool isGenerated,
        bool isArray)
        : this(
            property.DeclaringType ?? throw new ArgumentException("Property must have a declaring type.", nameof(property)),
            property.Name,
            property.PropertyType,
            columnName,
            typeName,
            isPrimaryKey,
            isIdentity,
            isGenerated,
            isArray)
    {
        _property = property;
    }

    private ColumnModel(
        Type declaringType,
        string propertyName,
        Type clrType,
        string columnName,
        string? typeName,
        bool isPrimaryKey,
        bool isIdentity,
        bool isGenerated,
        bool isArray)
    {
        DeclaringType = declaringType;
        PropertyName = propertyName;
        ClrType = clrType;
        ColumnName = columnName;
        TypeName = typeName;
        IsPrimaryKey = isPrimaryKey;
        IsIdentity = isIdentity;
        IsGenerated = isGenerated;
        IsArray = isArray;
    }

    public PropertyInfo Property
    {
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "This is the reflection fallback for execution paths not yet backed by generated readers/writers. Generated SQL metadata does not require PropertyInfo.")]
        get => _property ??= DeclaringType.GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{DeclaringType.Name}.{PropertyName}' is not available.");
    }

    public Type DeclaringType { get; }

    public string PropertyName { get; }

    public Type ClrType { get; }

    public string ColumnName { get; }

    public string? TypeName { get; }

    public bool IsPrimaryKey { get; }

    public bool IsIdentity { get; }

    public bool IsGenerated { get; }

    public bool IsArray { get; }

    public bool IsWritable => !IsIdentity && !IsGenerated;

    public static ColumnModel CreateGenerated<TDeclaring, TProperty>(
        string propertyName,
        string columnName,
        string? typeName,
        bool isPrimaryKey,
        bool isIdentity,
        bool isGenerated,
        bool isArray)
    {
        return new ColumnModel(
            typeof(TDeclaring),
            propertyName,
            typeof(TProperty),
            columnName,
            typeName,
            isPrimaryKey,
            isIdentity,
            isGenerated,
            isArray);
    }
}
