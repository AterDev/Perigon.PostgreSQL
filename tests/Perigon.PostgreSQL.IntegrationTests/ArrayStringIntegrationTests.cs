using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ArrayStringIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public ArrayStringIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Array_string_contains_uses_database_filter_and_paging()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var marker = "array-string-" + Guid.NewGuid().ToString("N");
        await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser { UserName = marker + "-percent", Age = 1, IsActive = true, Status = marker, Tags = ["100% ready"] },
            new IntegrationUser { UserName = marker + "-plain", Age = 2, IsActive = true, Status = marker, Tags = ["100X ready"] },
            new IntegrationUser { UserName = marker + "-slash", Age = 3, IsActive = true, Status = marker, Tags = [@"a\b"] },
            new IntegrationUser { UserName = marker + "-null", Age = 4, IsActive = true, Status = marker, Tags = null },
            new IntegrationUser { UserName = marker + "-prefix", Age = 5, IsActive = true, Status = marker, Tags = ["_tag-value"] },
            new IntegrationUser { UserName = marker + "-suffix", Age = 6, IsActive = true, Status = marker, Tags = ["value_tag"] },
            new IntegrationUser { UserName = marker + "-empty", Age = 7, IsActive = true, Status = marker, Tags = [""] }
        ]);

        try
        {
            var literalPercent = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.Contains("%")))
                .OrderBy(user => user.Age)
                .Skip(0)
                .Take(10)
                .ToListAsync();
            Assert.Single(literalPercent);
            Assert.Equal(marker + "-percent", literalPercent[0].UserName);

            var escapedSlash = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.Contains(@"\")))
                .OrderBy(user => user.Age)
                .Skip(0)
                .Take(10)
                .ToListAsync();
            Assert.Single(escapedSlash);
            Assert.Equal(marker + "-slash", escapedSlash[0].UserName);

            var startsWithLiteralUnderscore = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.StartsWith("_tag")))
                .ToListAsync();
            Assert.Single(startsWithLiteralUnderscore);
            Assert.Equal(marker + "-prefix", startsWithLiteralUnderscore[0].UserName);

            var endsWithLiteralUnderscore = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.EndsWith("_tag")))
                .ToListAsync();
            Assert.Single(endsWithLiteralUnderscore);
            Assert.Equal(marker + "-suffix", endsWithLiteralUnderscore[0].UserName);

            var emptySearch = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.Contains("")))
                .ToListAsync();
            Assert.Equal(6, emptySearch.Count);

            var paged = await db.IntegrationUsers
                .Where(user => user.Status == marker && user.Tags!.Any(tag => tag.Contains("ready")))
                .OrderBy(user => user.Age)
                .Skip(1)
                .Take(1)
                .ToListAsync();
            Assert.Single(paged);
            Assert.Equal(marker + "-plain", paged[0].UserName);
        }
        finally
        {
            await db.IntegrationUsers.Where(user => user.Status == marker).ExecuteDeleteAsync();
        }
    }
}
