namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class UnicodeAttribute : Attribute
{
    public UnicodeAttribute(bool isUnicode = true)
    {
        IsUnicode = isUnicode;
    }

    public bool IsUnicode { get; }
}