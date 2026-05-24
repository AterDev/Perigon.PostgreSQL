using System.Reflection;

namespace Perigon.PostgreSQL.Metadata;

public sealed class ColumnModel
{
    public ColumnModel(
        PropertyInfo property,
        string columnName,
        string? typeName,
        bool isPrimaryKey,
        bool isIdentity,
        bool isGenerated,
        bool isArray)
    {
        Property = property;
        ColumnName = columnName;
        TypeName = typeName;
        IsPrimaryKey = isPrimaryKey;
        IsIdentity = isIdentity;
        IsGenerated = isGenerated;
        IsArray = isArray;
    }

    public PropertyInfo Property { get; }

    public string PropertyName => Property.Name;

    public Type ClrType => Property.PropertyType;

    public string ColumnName { get; }

    public string? TypeName { get; }

    public bool IsPrimaryKey { get; }

    public bool IsIdentity { get; }

    public bool IsGenerated { get; }

    public bool IsArray { get; }

    public bool IsWritable => !IsIdentity && !IsGenerated;
}
