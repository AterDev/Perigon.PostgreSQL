namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class PrecisionAttribute : Attribute
{
    public PrecisionAttribute(int precision)
    {
        Precision = precision;
    }

    public PrecisionAttribute(int precision, int scale)
    {
        Precision = precision;
        Scale = scale;
    }

    public int Precision { get; }

    public int? Scale { get; }
}