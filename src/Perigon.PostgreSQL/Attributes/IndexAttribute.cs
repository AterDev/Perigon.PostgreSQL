namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IndexAttribute : Attribute
{
    public IndexAttribute(params string[] propertyNames)
    {
        PropertyNames = propertyNames ?? throw new ArgumentNullException(nameof(propertyNames));
    }

    public IReadOnlyList<string> PropertyNames { get; }

    public string? Name { get; init; }

    public bool IsUnique { get; init; }

    /// <summary>
    /// Optional PostgreSQL predicate applied to the index.
    /// </summary>
    public string? Filter { get; init; }
}
