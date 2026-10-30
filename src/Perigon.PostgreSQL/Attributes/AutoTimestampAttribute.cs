namespace Perigon.PostgreSQL.Attributes;

/// <summary>Generates a UTC timestamp on the client for the selected write operations.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class AutoTimestampAttribute : Attribute
{
    public AutoTimestampAttribute(bool onInsert = true, bool onUpdate = false)
    {
        OnInsert = onInsert;
        OnUpdate = onUpdate;
    }

    public bool OnInsert { get; }
    public bool OnUpdate { get; }
}
