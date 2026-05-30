namespace Perigon.PostgreSQL.Metadata;

public sealed class IndexDefinition
{
    public IndexDefinition(
        string? name,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        IReadOnlyList<string>? includePropertyNames = null,
        string? filter = null,
        string? method = null)
    {
        Name = name;
        PropertyNames = propertyNames;
        IsUnique = isUnique;
        IncludePropertyNames = includePropertyNames ?? [];
        Filter = filter;
        Method = method;
    }

    public string? Name { get; }

    public IReadOnlyList<string> PropertyNames { get; }

    public bool IsUnique { get; }

    public IReadOnlyList<string> IncludePropertyNames { get; }

    public string? Filter { get; }

    public string? Method { get; }
}