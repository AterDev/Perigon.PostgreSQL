using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Migrations;
using Perigon.PostgreSQL.NativeAotSmoke;
using Perigon.PostgreSQL.RawSql;

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

var registeredMigrations = MigrationRegistry.GetContextMigrations<SmokeDbContext>();
if (registeredMigrations.Count != 1 || registeredMigrations[0].Id != "0001_aot_registry")
{
    throw new InvalidOperationException("NativeAOT smoke test expected source-generated static migration registration.");
}

if (!EntityMaterializerRegistry.TryGet<SmokeProjection>(out _))
{
    throw new InvalidOperationException("NativeAOT smoke test expected generated DTO materializer.");
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

var connectionString = Environment.GetEnvironmentVariable("PERIGON_AOT_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    await using var liveDb = new SmokeDbContext(connectionString);
    await liveDb.EnsureCreatedAsync();
    await liveDb.Users.BulkInsertAsync(
    [
        new SmokeUser { UserName = "aot-live", Age = 21, Tags = ["aot", "live"] }
    ]);

    var liveRows = await liveDb.Users
        .Where(user => user.UserName == "aot-live")
        .ToListAsync();
    if (liveRows.Count != 1)
    {
        throw new InvalidOperationException("NativeAOT live smoke expected one inserted row.");
    }

    var liveUserName = "aot-live";
    var projections = await liveDb
        .SqlQuery<SmokeProjection>($"SELECT id, user_name FROM smoke_users WHERE user_name = {liveUserName}")
        .ToListAsync();
    if (projections.Count != 1 || projections[0].UserName != "aot-live")
    {
        throw new InvalidOperationException("NativeAOT live smoke expected the generated DTO materializer.");
    }

    Console.WriteLine("NativeAOT live database smoke passed.");
}
