using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationDbContext : DbContext
{
    public IntegrationDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<IntegrationUser> IntegrationUsers => Set<IntegrationUser>();

    public DbSet<IntegrationBlog> IntegrationBlogs => Set<IntegrationBlog>();
}
