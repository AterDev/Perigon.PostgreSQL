using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class IncludeManyIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public IncludeManyIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Include_many_loads_child_collections_with_split_query()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var user = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Alice",
            Age = 38,
            IsActive = true,
            Status = "include",
            CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = """{"include":true}"""
        });

        _ = await db.IntegrationBlogs.InsertManyReturningAsync(
        [
            new IntegrationBlog
            {
                IntegrationUserId = user.Id,
                Name = "AOT PostgreSQL",
                IsPublic = true,
                CreatedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)
            },
            new IntegrationBlog
            {
                IntegrationUserId = user.Id,
                Name = "NativeAOT Notes",
                IsPublic = false,
                CreatedAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc)
            }
        ]);

        var graph = await db.IntegrationUsers
            .Where(u => u.UserName == "Include-Alice")
            .IncludeManyAsync(
                db.IntegrationBlogs,
                u => u.Id,
                b => b.IntegrationUserId,
                (u, blogs) => new UserWithBlogs(u, blogs));

        var row = Assert.Single(graph);
        Assert.Equal("Include-Alice", row.User.UserName);
        Assert.Equal(2, row.Blogs.Count);
        Assert.Contains(row.Blogs, b => b.Name == "AOT PostgreSQL");
        Assert.Contains(row.Blogs, b => b.Name == "NativeAOT Notes");
    }

    [Fact]
    public async Task Include_many_returns_empty_child_collection_when_no_children_match()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Empty",
            Age = 39,
            IsActive = true,
            Status = "include-empty",
            CreatedAt = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = """{"include":false}"""
        });

        var graph = await db.IntegrationUsers
            .Where(u => u.UserName == "Include-Empty")
            .IncludeManyAsync(
                db.IntegrationBlogs,
                u => u.Id,
                b => b.IntegrationUserId,
                (u, blogs) => new UserWithBlogs(u, blogs));

        var row = Assert.Single(graph);
        Assert.Empty(row.Blogs);
    }

    [Fact]
    public async Task Include_many_applies_child_filter_and_order_in_split_query()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var user = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Filtered",
            Age = 41,
            IsActive = true,
            Status = "include-filtered",
            CreatedAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = """{"include":"filtered"}"""
        });

        _ = await db.IntegrationBlogs.InsertManyReturningAsync(
        [
            new IntegrationBlog
            {
                IntegrationUserId = user.Id,
                Name = "Z Private",
                IsPublic = false,
                CreatedAt = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc)
            },
            new IntegrationBlog
            {
                IntegrationUserId = user.Id,
                Name = "B Public",
                IsPublic = true,
                CreatedAt = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc)
            },
            new IntegrationBlog
            {
                IntegrationUserId = user.Id,
                Name = "A Public",
                IsPublic = true,
                CreatedAt = new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc)
            }
        ]);

        var graph = await db.IntegrationUsers
            .Where(u => u.UserName == "Include-Filtered")
            .IncludeManyAsync(
                db.IntegrationBlogs,
                u => u.Id,
                b => b.IntegrationUserId,
                blogs => blogs.Where(b => b.IsPublic).OrderBy(b => b.Name),
                (u, blogs) => new UserWithBlogs(u, blogs));

        var row = Assert.Single(graph);
        Assert.Equal(["A Public", "B Public"], row.Blogs.Select(b => b.Name));
    }
}
