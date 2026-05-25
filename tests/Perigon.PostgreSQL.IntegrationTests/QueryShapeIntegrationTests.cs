using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Execution;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class QueryShapeIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public QueryShapeIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Inner_join_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var user = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Join-Projection",
            Age = 44,
            IsActive = true,
            Status = "join-projection",
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["join"],
            ProfileJson = """{"join":true}"""
        });

        var blog = await db.IntegrationBlogs.InsertAsync(new IntegrationBlog
        {
            IntegrationUserId = user.Id,
            Name = "Join Blog",
            IsPublic = true,
            CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
        });

        var rows = await db.IntegrationUsers
            .Where(u => u.UserName == "Join-Projection")
            .Join(
                db.IntegrationBlogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.IntegrationUserId,
                (u, b) => new IntegrationUserBlogRow
                {
                    UserId = u.Id,
                    UserName = u.UserName,
                    BlogId = b.Id,
                    BlogName = b.Name
                })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal("Join-Projection", row.UserName);
        Assert.Equal(blog.Id, row.BlogId);
        Assert.Equal("Join Blog", row.BlogName);
    }

    [Fact]
    public async Task Multi_key_group_by_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        Assert.True(EntityMaterializerRegistry.TryGet<IntegrationUserStatusStat>(out _));

        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Group-Multi-A",
                Age = 20,
                IsActive = true,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":1}"""
            },
            new IntegrationUser
            {
                UserName = "Group-Multi-B",
                Age = 30,
                IsActive = true,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":2}"""
            },
            new IntegrationUser
            {
                UserName = "Group-Multi-C",
                Age = 40,
                IsActive = false,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "group-multi")
            .GroupBy(u => new { u.Status, u.IsActive })
            .Select(g => new IntegrationUserStatusStat
            {
                Status = g.Key.Status,
                IsActive = g.Key.IsActive,
                Count = g.LongCount(),
                AverageAge = g.Average(u => u.Age)
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.IsActive)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var active = rows.Single(row => row.IsActive);
        Assert.Equal("group-multi", active.Status);
        Assert.Equal(2, active.Count);
        Assert.Equal(25d, active.AverageAge);

        var inactive = rows.Single(row => !row.IsActive);
        Assert.Equal(1, inactive.Count);
        Assert.Equal(40d, inactive.AverageAge);
    }

    [Fact]
    public async Task Distinct_scalar_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Distinct-A",
                Age = 22,
                IsActive = true,
                Status = "distinct-status",
                CreatedAt = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["distinct"],
                ProfileJson = """{"distinct":1}"""
            },
            new IntegrationUser
            {
                UserName = "Distinct-B",
                Age = 23,
                IsActive = true,
                Status = "distinct-status",
                CreatedAt = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["distinct"],
                ProfileJson = """{"distinct":2}"""
            }
        ]);

        var statuses = await db.IntegrationUsers
            .Where(u => u.Tags!.Contains("distinct"))
            .Select(u => u.Status)
            .Distinct()
            .ToScalarListAsync();

        Assert.Equal(["distinct-status"], statuses);
    }

    [Fact]
    public async Task Count_distinct_group_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "CountDistinct-A",
                Age = 24,
                IsActive = true,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":1}"""
            },
            new IntegrationUser
            {
                UserName = "CountDistinct-B",
                Age = 25,
                IsActive = false,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":2}"""
            },
            new IntegrationUser
            {
                UserName = "CountDistinct-C",
                Age = 26,
                IsActive = false,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "count-distinct")
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationDistinctStatusStat
            {
                Status = g.Key,
                DistinctActiveStates = g.LongCountDistinct(u => u.IsActive)
            })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("count-distinct", row.Status);
        Assert.Equal(2, row.DistinctActiveStates);
    }

    [Fact]
    public async Task Native_aggregate_group_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "NativeAgg-A",
                Age = 27,
                IsActive = true,
                Status = "native-aggregate",
                CreatedAt = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["native-aggregate"],
                ProfileJson = """{"nativeAgg":1}"""
            },
            new IntegrationUser
            {
                UserName = "NativeAgg-B",
                Age = 28,
                IsActive = true,
                Status = "native-aggregate",
                CreatedAt = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["native-aggregate"],
                ProfileJson = """{"nativeAgg":2}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "native-aggregate")
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationAggregatePayload
            {
                Status = g.Key,
                Names = g.ArrayAgg(u => u.UserName),
                Profiles = g.JsonbAgg(u => u.ProfileJson)
            })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("native-aggregate", row.Status);
        Assert.Equal(["NativeAgg-A", "NativeAgg-B"], row.Names.Order().ToArray());
        Assert.Contains("\"nativeAgg\": 1", row.Profiles, StringComparison.Ordinal);
        Assert.Contains("\"nativeAgg\": 2", row.Profiles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_by_having_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Having-A",
                Age = 29,
                IsActive = true,
                Status = "having-match",
                CreatedAt = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":1}"""
            },
            new IntegrationUser
            {
                UserName = "Having-B",
                Age = 30,
                IsActive = true,
                Status = "having-match",
                CreatedAt = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":2}"""
            },
            new IntegrationUser
            {
                UserName = "Having-C",
                Age = 31,
                IsActive = true,
                Status = "having-miss",
                CreatedAt = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status!.StartsWith("having-"))
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationUserStatusStat
            {
                Status = g.Key,
                Count = g.LongCount()
            })
            .Where(x => x.Count >= 2)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("having-match", row.Status);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task Jsonb_path_exists_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "JsonPath-Alice",
            Age = 45,
            IsActive = true,
            Status = "jsonpath",
            CreatedAt = new DateTime(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["jsonpath"],
            ProfileJson = """{"level":7,"name":"JsonPath-Alice"}"""
        });

        var users = await db.IntegrationUsers
            .Where(u => u.ProfileJson.JsonbPathExists("$.level ? (@ > 5)"))
            .ToListAsync();

        Assert.Contains(users, u => u.UserName == "JsonPath-Alice");
    }
}
