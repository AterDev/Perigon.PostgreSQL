using System.ComponentModel.DataAnnotations.Schema;
using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NavigationIncludeIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Include_loads_collection_and_preserves_empty_parent()
    {
        await using var db = new NavigationTestDbContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();
        var owner = await db.Owners.InsertAsync(new NavigationOwner { Name = "with-posts" });
        var empty = await db.Owners.InsertAsync(new NavigationOwner { Name = "without-posts" });
        await db.Posts.InsertAsync(new NavigationPost { NavigationOwnerId = owner.Id, Title = "first" });
        await db.Posts.InsertAsync(new NavigationPost { NavigationOwnerId = owner.Id, Title = "second" });

        var owners = await db.Owners.Include(o => o.Posts)
            .Where(o => o.Id == owner.Id || o.Id == empty.Id).OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(2, owners.Count);
        Assert.Equal(2, owners.Single(o => o.Id == owner.Id).Posts.Count);
        Assert.Empty(owners.Single(o => o.Id == empty.Id).Posts);

        var posts = await db.Posts.Include(p => p.NavigationOwner)
            .Where(p => p.NavigationOwnerId == owner.Id).ToListAsync();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, post => Assert.Equal(owner.Id, post.NavigationOwner?.Id));

        await db.Posts.InsertAsync(new NavigationPost { NavigationOwnerId = empty.Id, Title = "third" });
        await db.Posts.InsertAsync(new NavigationPost { NavigationOwnerId = empty.Id, Title = "fourth" });
        var excluded = "first";
        var filtered = await db.Owners.Where(o => o.Id == owner.Id || o.Id == empty.Id)
            .Include(o => o.Posts.Where(p => p.Title != excluded).OrderBy(p => p.Title).Take(1)).ToListAsync();
        Assert.Equal("second", Assert.Single(filtered.Single(o => o.Id == owner.Id).Posts).Title);
        Assert.Equal("fourth", Assert.Single(filtered.Single(o => o.Id == empty.Id).Posts).Title);
    }
}

public sealed class NavigationTestDbContext(string connectionString) : DbContext(options => options.UsePostgres(connectionString))
{
    public DbSet<NavigationOwner> Owners => Set<NavigationOwner>();
    public DbSet<NavigationPost> Posts => Set<NavigationPost>();
}

[Table("navigation_include_owners")]
public sealed class NavigationOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [NotMapped] public List<NavigationPost> Posts { get; set; } = [];
}

[Table("navigation_include_posts")]
public sealed class NavigationPost
{
    public int Id { get; set; }
    public int NavigationOwnerId { get; set; }
    public string Title { get; set; } = "";
    [NotMapped] public NavigationOwner? NavigationOwner { get; set; }
}
