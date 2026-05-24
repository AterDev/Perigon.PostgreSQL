using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Metadata;

public sealed class EntityModel
{
    private EntityModel(Type clrType, string? schema, string tableName, IReadOnlyList<ColumnModel> columns)
    {
        ClrType = clrType;
        Schema = schema;
        TableName = tableName;
        Columns = columns;
        PrimaryKey = columns.FirstOrDefault(c => c.IsPrimaryKey);
    }

    public Type ClrType { get; }

    public string? Schema { get; }

    public string TableName { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public ColumnModel? PrimaryKey { get; }

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
        return Build(entityType);
    }

    private static EntityModel Build(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type entityType)
    {
        var table = entityType.GetCustomAttribute<TableAttribute>();
        var tableName = table?.Name ?? NamingConventions.DefaultTableName(entityType);
        var schema = table?.Schema;
        var properties = entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is not null && p.GetCustomAttribute<NotMappedAttribute>() is null)
            .ToArray();

        var columns = new List<ColumnModel>(properties.Length);
        foreach (var property in properties)
        {
            var column = property.GetCustomAttribute<ColumnAttribute>();
            var isPrimaryKey = column?.IsPrimaryKey == true ||
                               property.Name.Equals("Id", StringComparison.Ordinal) ||
                               property.Name.Equals(entityType.Name + "Id", StringComparison.Ordinal);

            columns.Add(new ColumnModel(
                property,
                column?.Name ?? NamingConventions.DefaultColumnName(property.Name),
                column?.TypeName,
                isPrimaryKey,
                column?.IsIdentity == true || (isPrimaryKey && IsInteger(property.PropertyType)),
                column?.IsGenerated == true,
                column?.IsArray == true || IsArrayLike(property.PropertyType)));
        }

        return new EntityModel(entityType, schema, tableName, columns);
    }

    private static bool IsInteger(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual == typeof(short) || actual == typeof(int) || actual == typeof(long);
    }

    private static bool IsArrayLike(Type type)
    {
        return type.IsArray ||
               (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));
    }
}
