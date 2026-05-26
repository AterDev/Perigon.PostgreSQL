using Perigon.PostgreSQL;
using Perigon.PostgreSQL.AspNetCoreSample.Models;

namespace Perigon.PostgreSQL.AspNetCoreSample.Data;

public sealed class SampleDbContext : DbContext
{
    public SampleDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<SampleUser> Users => Set<SampleUser>();

    public DbSet<SampleBlog> Blogs => Set<SampleBlog>();

    public DbSet<SamplePost> Posts => Set<SamplePost>();
}
