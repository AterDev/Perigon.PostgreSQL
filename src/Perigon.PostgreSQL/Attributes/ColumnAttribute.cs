namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute()
    {
    }

    public ColumnAttribute(string name)
    {
        Name = name;
    }

    public string? Name { get; init; }

    public string? TypeName { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsGenerated { get; init; }

    public bool IsArray { get; init; }
}
