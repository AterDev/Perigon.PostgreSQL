using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Migrations;
using Perigon.PostgreSQL.NativeAotSmoke;
using Perigon.PostgreSQL.RawSql;
using System.Linq.Expressions;

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
using var auditDb = new SmokeAuditContext();
var auditModel = auditDb.Items.Model;
var auditItem = new SmokeAuditItem();
var filteredAudit = auditDb.Items.ToQuerySql();
if (!filteredAudit.CommandText.Contains("WHERE", StringComparison.Ordinal) ||
    !filteredAudit.Parameters.Any(parameter => Equals(parameter.Value, "hidden")))
    throw new InvalidOperationException("NativeAOT smoke expected a parameterized global filter.");
var generatedId = Guid.CreateVersion7();
EntityValueAccessorRegistry.SetValue(auditModel.GetColumn(nameof(SmokeAuditItem.Id)), auditItem, generatedId);
if (auditItem.Id != generatedId || !auditModel.GetColumn(nameof(SmokeAuditItem.UpdatedTime)).TimestampOnUpdate)
    throw new InvalidOperationException("NativeAOT smoke expected inherited generated setters and timestamp metadata.");
var auditUpdate = auditDb.Items.Where(item => item.Id == generatedId).ToUpdateSql(set => set.Set(item => item.Name, "aot"));
if (!auditUpdate.CommandText.Contains("updated_time", StringComparison.Ordinal))
    throw new InvalidOperationException("NativeAOT smoke expected an automatic update timestamp.");
using var navigationDb = new SmokeNavigationContext();
var navigationSql = navigationDb.Owners.Include(owner => owner.Posts).Where(owner => owner.Id > 0).ToQuerySql();
if (!navigationSql.CommandText.Contains("WHERE", StringComparison.Ordinal))
    throw new InvalidOperationException("NativeAOT smoke test expected a generated navigation Include.");
_ = navigationDb.Posts.Include(post => post.SmokeOwner).ToQuerySql();
Expression<Func<SmokeOwner, IEnumerable<SmokePost>>> filteredNavigation = owner => owner.Posts.Where(post => post.Id > 0).OrderBy(post => post.Id).Take(1);
_ = navigationDb.Owners.Include(filteredNavigation).ToQuerySql();
await NavigationIncludeRegistry.GetLoader<SmokeOwner>(typeof(SmokeNavigationContext), nameof(SmokeOwner.Posts))(navigationDb, [], filteredNavigation.Body, CancellationToken.None);
await NavigationIncludeRegistry.GetLoader<SmokePost>(typeof(SmokeNavigationContext), nameof(SmokePost.SmokeOwner))(navigationDb, [], null, CancellationToken.None);
var minimumAge = 18;
var requiredTag = "aot";
var sql = db.Users
    .Where(u => u.Age > minimumAge && u.Tags!.Contains(requiredTag))
    .OrderBy(u => u.UserName)
    .Take(5)
    .ToQuerySql();

Console.WriteLine(sql.CommandText);
Console.WriteLine(sql.Parameters.Count);

var projectedSql = AotExpressionTreeSmoke.BuildProjectedSql(db, minimumAge);
if (!projectedSql.CommandText.Contains("FROM (SELECT", StringComparison.Ordinal) ||
    !projectedSql.CommandText.Contains("p.\"UserName\"", StringComparison.Ordinal) ||
    projectedSql.Parameters.Count != 3)
{
    throw new InvalidOperationException("NativeAOT smoke test expected a parameterized post-projection query.");
}

var correlatedSql = db.Users
    .Where(user => db.Users.Any(other => other.Id == user.Id && other.Age > minimumAge))
    .ToQuerySql();
if (!correlatedSql.CommandText.Contains("EXISTS (SELECT 1", StringComparison.Ordinal) ||
    correlatedSql.Parameters.Count != 1)
{
    throw new InvalidOperationException("NativeAOT smoke test expected a parameterized correlated subquery.");
}

var countSql = db.Users.Where(user => db.Users.LongCount(other => other.Id == user.Id) > 0L).ToQuerySql();
if (!countSql.CommandText.Contains("SELECT count(*)", StringComparison.Ordinal))
{
    throw new InvalidOperationException("NativeAOT smoke test expected a correlated count subquery.");
}
var aggregateSql = AotExpressionTreeSmoke.BuildAggregateSql(db);
if (!aggregateSql.CommandText.Contains("sum(", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("NativeAOT smoke test expected an aggregate projection.");

static IQueryable<SmokeUser> RebuildQuery(SmokeDbContext context, int age) =>
    context.Users.Where(user => user.Age >= age).OrderBy(user => user.UserName).Take(5);

var cachedFirst = RebuildQuery(db, 18).ToQuerySql();
var cachedSecond = RebuildQuery(db, 25).ToQuerySql();
if (cachedFirst.CommandText != cachedSecond.CommandText ||
    !cachedSecond.CommandText.Contains("e.\"user_name\"", StringComparison.Ordinal) ||
    cachedSecond.Parameters[0].Value is not 25)
{
    throw new InvalidOperationException("NativeAOT smoke test expected rebuilt query parameters and mapped columns to remain correct.");
}

var connectionString = Environment.GetEnvironmentVariable("PERIGON_AOT_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    await using var navigationLive = new SmokeNavigationContext(connectionString);
    await navigationLive.EnsureCreatedAsync();
    var liveOwner = await navigationLive.Owners.InsertAsync(new SmokeOwner { Name = "aot-owner" });
    await navigationLive.Posts.InsertAsync(new SmokePost { SmokeOwnerId = liveOwner.Id });
    var owners = await navigationLive.Owners.Include(owner => owner.Posts.Where(post => post.Id > 0).OrderBy(post => post.Id).Take(1))
        .Where(owner => owner.Id == liveOwner.Id).ToListAsync();
    if (owners.Count != 1 || owners[0].Posts.Count != 1)
        throw new InvalidOperationException("NativeAOT live smoke expected the included collection.");

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

    var aggregates = await AotExpressionTreeSmoke.LoadAggregatesAsync(liveDb);
    if (aggregates.Count != 1 || aggregates[0].Count != 1 || aggregates[0].Count32 != 1 || aggregates[0].Sum != 21 ||
        aggregates[0].Minimum != 21 || aggregates[0].Maximum != 21 || aggregates[0].Average != 21d || aggregates[0].MinimumRequired != 21)
        throw new InvalidOperationException("NativeAOT live smoke expected correlated aggregate results.");

    var emptyAggregateThrew = false;
    try
    {
        await AotExpressionTreeSmoke.LoadEmptyAggregateAsync(liveDb);
    }
    catch (InvalidOperationException exception) when (exception.Message == "Sequence contains no elements.")
    {
        emptyAggregateThrew = true;
    }
    if (!emptyAggregateThrew)
        throw new InvalidOperationException("NativeAOT live smoke expected empty aggregate to throw.");

    var related = await liveDb.Users.Where(user => user.UserName == "aot-live")
        .IncludeAsync(liveDb.Users, liveDb.Users, user => user.Id, link => link.Id,
            link => link.Id, child => child.Id, child => child.Age > 18, (user, children) => children);
    if (related.Count != 1 || related[0].Count != 1)
    {
        throw new InvalidOperationException("NativeAOT live smoke expected one related row.");
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
