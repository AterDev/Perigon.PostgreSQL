namespace Microsoft.EntityFrameworkCore;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IndexAttribute : Attribute
{
    public IndexAttribute(params string[] propertyNames)
    {
        PropertyNames = propertyNames;
    }

    public IReadOnlyList<string> PropertyNames { get; }

    public string? Name { get; init; }

    public bool IsUnique { get; init; }
}