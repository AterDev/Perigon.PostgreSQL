using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RebuiltQueryCacheIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Rebuilt_simple_queries_rebind_values_and_preserve_order_and_take()
    {
        await using var db = new IntegrationDbContext(fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Rebuilt-Cache-Young",
                Age = 21,
                IsActive = true,
                Status = "rebuilt-cache",
                CreatedAt = new DateTime(2097, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["rebuilt-cache"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "Rebuilt-Cache-Middle",
                Age = 41,
                IsActive = true,
                Status = "rebuilt-cache",
                CreatedAt = new DateTime(2098, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["rebuilt-cache"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "Rebuilt-Cache-Older",
                Age = 61,
                IsActive = true,
                Status = "rebuilt-cache",
                CreatedAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["rebuilt-cache"],
                ProfileJson = "{}"
            }
        ]);

        var first = await BuildQuery(db, 20).ToListAsync();
        var second = await BuildQuery(db, 40).ToListAsync();
        var third = await BuildQuery(db, 60).ToListAsync();

        Assert.Equal(["Rebuilt-Cache-Older", "Rebuilt-Cache-Middle"], first.Select(user => user.UserName));
        Assert.Equal(["Rebuilt-Cache-Older", "Rebuilt-Cache-Middle"], second.Select(user => user.UserName));
        Assert.Equal(["Rebuilt-Cache-Older"], third.Select(user => user.UserName));
    }

    private static IQueryable<IntegrationUser> BuildQuery(IntegrationDbContext db, int minimumAge)
    {
        return db.IntegrationUsers
            .Where(user => user.IsActive && user.Age >= minimumAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(2);
    }
}
