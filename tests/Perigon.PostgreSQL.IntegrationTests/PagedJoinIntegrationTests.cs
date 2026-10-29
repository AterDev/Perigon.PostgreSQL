using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PagedJoinIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Correlated_long_count_filters_by_matching_rows()
    {
        await using var db = new IntegrationDbContext(fixture.ConnectionString);
        var parent = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "count-subquery", Age = 30, IsActive = true, Status = "count-subquery",
            CreatedAt = DateTime.UtcNow, Tags = [], ProfileJson = "{}"
        });
        await db.IntegrationBlogs.InsertAsync(new IntegrationBlog { IntegrationUserId = parent.Id, Name = "match", IsPublic = true, CreatedAt = DateTime.UtcNow });
        await db.IntegrationBlogs.InsertAsync(new IntegrationBlog { IntegrationUserId = parent.Id, Name = "hidden", IsPublic = false, CreatedAt = DateTime.UtcNow });
        var rows = await db.IntegrationUsers.Where(u => u.Id == parent.Id &&
            db.IntegrationBlogs.LongCount(b => b.IntegrationUserId == u.Id && b.IsPublic) == 1L).ToListAsync();
        Assert.Equal(parent.Id, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task Outer_take_precedes_join_and_preserves_all_matching_children()
    {
        await using var db = new IntegrationDbContext(fixture.ConnectionString);
        var parent = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "paged-join", Age = 30, IsActive = true, Status = "paged-join",
            CreatedAt = DateTime.UtcNow, Tags = [], ProfileJson = "{}"
        });
        await db.IntegrationBlogs.InsertAsync(new IntegrationBlog { IntegrationUserId = parent.Id, Name = "first", IsPublic = true, CreatedAt = DateTime.UtcNow });
        await db.IntegrationBlogs.InsertAsync(new IntegrationBlog { IntegrationUserId = parent.Id, Name = "second", IsPublic = true, CreatedAt = DateTime.UtcNow });
        var rows = await db.IntegrationUsers.Where(u => u.Id == parent.Id).OrderBy(u => u.Id).Take(1)
            .Join(db.IntegrationBlogs, u => u.Id, b => b.IntegrationUserId,
                (u, b) => new IntegrationUserBlogRow { UserId = u.Id, UserName = u.UserName, BlogId = b.Id, BlogName = b.Name })
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(parent.Id, row.UserId));
    }
}
