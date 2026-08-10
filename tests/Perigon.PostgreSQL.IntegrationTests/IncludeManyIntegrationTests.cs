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

    [Fact]
    public async Task Include_many_applies_take_per_parent()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var first = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Paged-A",
            Age = 42,
            IsActive = true,
            Status = "include-paged",
            CreatedAt = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = """{"include":"paged-a"}"""
        });
        var second = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Paged-B",
            Age = 43,
            IsActive = true,
            Status = "include-paged",
            CreatedAt = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = """{"include":"paged-b"}"""
        });

        _ = await db.IntegrationBlogs.InsertManyReturningAsync(
        [
            new IntegrationBlog { IntegrationUserId = first.Id, Name = "A First", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = first.Id, Name = "B First", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = second.Id, Name = "A Second", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = second.Id, Name = "B Second", IsPublic = true, CreatedAt = DateTime.UtcNow }
        ]);

        var graph = await db.IntegrationUsers
            .Where(user => user.Status == "include-paged")
            .OrderBy(user => user.UserName)
            .IncludeManyAsync(
                db.IntegrationBlogs,
                user => user.Id,
                blog => blog.IntegrationUserId,
                blogs => blogs.OrderBy(blog => blog.Name).Take(1),
                (user, blogs) => new UserWithBlogs(user, blogs));

        Assert.Equal(2, graph.Count);
        Assert.Equal(["A First"], graph[0].Blogs.Select(blog => blog.Name));
        Assert.Equal(["A Second"], graph[1].Blogs.Select(blog => blog.Name));
    }

    [Fact]
    public async Task Include_many_applies_skip_per_parent()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var first = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Skip-A",
            Age = 44,
            IsActive = true,
            Status = "include-skip",
            CreatedAt = new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = "{\"include\":\"skip-a\"}"
        });
        var second = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Include-Skip-B",
            Age = 45,
            IsActive = true,
            Status = "include-skip",
            CreatedAt = new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["include"],
            ProfileJson = "{\"include\":\"skip-b\"}"
        });

        _ = await db.IntegrationBlogs.InsertManyReturningAsync(
        [
            new IntegrationBlog { IntegrationUserId = first.Id, Name = "A First", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = first.Id, Name = "B First", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = first.Id, Name = "C First", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = second.Id, Name = "A Second", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = second.Id, Name = "B Second", IsPublic = true, CreatedAt = DateTime.UtcNow },
            new IntegrationBlog { IntegrationUserId = second.Id, Name = "C Second", IsPublic = true, CreatedAt = DateTime.UtcNow }
        ]);

        var graph = await db.IntegrationUsers
            .Where(user => user.Status == "include-skip")
            .OrderBy(user => user.UserName)
            .IncludeManyAsync(
                db.IntegrationBlogs,
                user => user.Id,
                blog => blog.IntegrationUserId,
                blogs => blogs.OrderBy(blog => blog.Name).Skip(1).Take(1),
                (user, blogs) => new UserWithBlogs(user, blogs));

        Assert.Equal(2, graph.Count);
        Assert.Equal(["B First"], graph[0].Blogs.Select(blog => blog.Name));
        Assert.Equal(["B Second"], graph[1].Blogs.Select(blog => blog.Name));
    }
}
