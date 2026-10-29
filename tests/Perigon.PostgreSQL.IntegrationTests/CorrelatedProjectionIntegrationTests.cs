using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CorrelatedProjectionIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Dto_projection_executes_correlated_long_count()
    {
        await using var db = new IntegrationDbContext(fixture.ConnectionString);
        var user = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "projection-count",
            Age = 40,
            IsActive = true,
            Status = "projection-count",
            CreatedAt = DateTime.UtcNow,
            Tags = [],
            ProfileJson = "{}"
        });
        var emptyUser = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "projection-count-empty",
            Age = 41,
            IsActive = true,
            Status = "projection-count-empty",
            CreatedAt = DateTime.UtcNow,
            Tags = [],
            ProfileJson = "{}"
        });
        await db.IntegrationBlogs.InsertAsync(new IntegrationBlog
        {
            IntegrationUserId = user.Id,
            Name = "projection-count-blog",
            IsPublic = true,
            CreatedAt = DateTime.UtcNow
        });

        var rows = await db.IntegrationUsers
            .Where(item => item.Id == user.Id || item.Id == emptyUser.Id)
            .Select(item => new IntegrationUserBlogCountRow
            {
                UserId = item.Id,
                BlogCount = db.IntegrationBlogs.LongCount(blog => blog.IntegrationUserId == item.Id),
                IntBlogCount = db.IntegrationBlogs.Count(blog => blog.IntegrationUserId == item.Id),
                BlogIdSum = db.IntegrationBlogs.Where(blog => blog.IntegrationUserId == item.Id).Sum(blog => blog.Id),
                BlogIdMin = db.IntegrationBlogs.Where(blog => blog.IntegrationUserId == item.Id).Min(blog => (int?)blog.Id),
                BlogIdMax = db.IntegrationBlogs.Where(blog => blog.IntegrationUserId == item.Id).Max(blog => (int?)blog.Id),
                BlogIdAverage = db.IntegrationBlogs.Where(blog => blog.IntegrationUserId == item.Id).Average(blog => (double?)blog.Id)
            })
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var row = Assert.Single(rows, item => item.UserId == user.Id);
        Assert.Equal(1L, row.BlogCount);
        Assert.Equal(1, row.IntBlogCount);
        Assert.True(row.BlogIdSum > 0);
        Assert.Equal(row.BlogIdMin, row.BlogIdMax);
        Assert.Equal(row.BlogIdMin, (int)row.BlogIdAverage!.Value);
        var emptyRow = Assert.Single(rows, item => item.UserId == emptyUser.Id);
        Assert.Equal(0L, emptyRow.BlogCount);
        Assert.Equal(0, emptyRow.IntBlogCount);
        Assert.Equal(0, emptyRow.BlogIdSum);
        Assert.Null(emptyRow.BlogIdMin);
        Assert.Null(emptyRow.BlogIdMax);
        Assert.Null(emptyRow.BlogIdAverage);
    }
}
