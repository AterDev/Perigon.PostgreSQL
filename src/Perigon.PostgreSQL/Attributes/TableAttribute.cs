namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string? Schema { get; init; }
}
