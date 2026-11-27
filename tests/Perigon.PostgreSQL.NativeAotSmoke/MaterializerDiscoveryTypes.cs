using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.NativeAotSmoke;

[assembly: GenerateMaterializer(typeof(ExplicitResult))]

namespace Perigon.PostgreSQL.NativeAotSmoke;

internal sealed class ExplicitResult
{
    public int Id { get; set; }
}

internal sealed class UnrelatedRequest
{
    public int Id { get; set; }
}
