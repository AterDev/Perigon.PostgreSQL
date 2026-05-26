namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ViewAttribute : Attribute
{
    public ViewAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string? Schema { get; init; }
}