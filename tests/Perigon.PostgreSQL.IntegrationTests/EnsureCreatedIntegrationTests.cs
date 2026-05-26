using Npgsql;
using Microsoft.EntityFrameworkCore;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Execution;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class EnsureCreatedIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public EnsureCreatedIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreated_creates_tables_and_convention_foreign_keys_idempotently()
    {
        await using var db = new EnsureCreatedDbContext(_fixture.ConnectionString);

        await db.EnsureCreatedAsync();
        await db.EnsureCreatedAsync();

        var user = await db.EnsureCreatedUsers.InsertAsync(new EnsureCreatedUser
        {
            UserName = "EnsureCreated Alice"
        });

        var blog = await db.EnsureCreatedBlogs.InsertAsync(new EnsureCreatedBlog
        {
            EnsureCreatedUserId = user.Id,
            Name = "Created by EnsureCreated"
        });

        var blogs = await db.EnsureCreatedBlogs.Where(item => item.Id == blog.Id).ToListAsync();
        Assert.Single(blogs);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.EnsureCreatedBlogs.InsertAsync(new EnsureCreatedBlog
            {
                EnsureCreatedUserId = -1,
                Name = "Invalid FK"
            }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);

        var unique = await Assert.ThrowsAsync<PostgresException>(() =>
            db.EnsureCreatedUsers.InsertAsync(new EnsureCreatedUser
            {
                UserName = "EnsureCreated Alice"
            }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
    }
}

public sealed class EnsureCreatedDbContext : DbContext
{
    public EnsureCreatedDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<EnsureCreatedUser> EnsureCreatedUsers => Set<EnsureCreatedUser>();

    public DbSet<EnsureCreatedBlog> EnsureCreatedBlogs => Set<EnsureCreatedBlog>();
}

[Index(nameof(UserName), Name = "uq_ensure_created_users_user_name", IsUnique = true)]
public sealed class EnsureCreatedUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";
}

public sealed class EnsureCreatedBlog
{
    public int Id { get; set; }

    public int EnsureCreatedUserId { get; set; }

    public string Name { get; set; } = "";
}