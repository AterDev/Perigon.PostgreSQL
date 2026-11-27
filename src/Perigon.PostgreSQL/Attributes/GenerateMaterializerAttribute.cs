namespace Perigon.PostgreSQL.Attributes;

/// <summary>Generates a materializer for a result type used through a query that cannot be resolved at compile time.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GenerateMaterializerAttribute(Type resultType) : Attribute
{
    public Type ResultType { get; } = resultType;
}
