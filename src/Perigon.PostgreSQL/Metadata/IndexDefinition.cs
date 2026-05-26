namespace Perigon.PostgreSQL.Metadata;

public sealed class IndexDefinition
{
    public IndexDefinition(string? name, IReadOnlyList<string> propertyNames, bool isUnique)
    {
        Name = name;
        PropertyNames = propertyNames;
        IsUnique = isUnique;
    }

    public string? Name { get; }

    public IReadOnlyList<string> PropertyNames { get; }

    public bool IsUnique { get; }
}