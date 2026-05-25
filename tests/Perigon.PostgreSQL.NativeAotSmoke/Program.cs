using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.NativeAotSmoke;

if (!EntityModel.For<SmokeUser>().IsGenerated)
{
    throw new InvalidOperationException("NativeAOT smoke test expected generated entity metadata.");
}

if (!EntityMaterializerRegistry.TryGet<SmokeUser>(out _))
{
    throw new InvalidOperationException("NativeAOT smoke test expected generated entity materializer.");
}

if (!EntityValueAccessorRegistry.TryGetAccessor(typeof(SmokeUser), nameof(SmokeUser.UserName), out _))
{
    throw new InvalidOperationException("NativeAOT smoke test expected generated entity value accessors.");
}

var db = new SmokeDbContext();
var minimumAge = 18;
var requiredTag = "aot";
var sql = db.Users
    .Where(u => u.Age > minimumAge && u.Tags!.Contains(requiredTag))
    .OrderBy(u => u.UserName)
    .Take(5)
    .ToQuerySql();

Console.WriteLine(sql.CommandText);
Console.WriteLine(sql.Parameters.Count);
