using Perigon.PostgreSQL;
using Perigon.PostgreSQL.AspNetCoreSample.Models;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.AspNetCoreSample.Data;

public sealed class SampleDbContext : DbContext
{
    public SampleDbContext(DbContextOptions<SampleDbContext> options)
        : base(options)
    {
    }

    public SampleDbContext(string connectionString)
        : base(builder => builder.UseNpgsql(connectionString))
    {
    }

    public DbSet<SampleUser> Users => Set<SampleUser>();

    public DbSet<SampleBlog> Blogs => Set<SampleBlog>();

    public DbSet<SamplePost> Posts => Set<SamplePost>();
}
