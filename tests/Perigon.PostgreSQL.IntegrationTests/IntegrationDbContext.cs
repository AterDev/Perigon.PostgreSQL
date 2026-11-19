using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

#pragma warning disable CS8618 // Initialized by the DbSet source generator.
public sealed class IntegrationDbContext : DbContext
{
    public IntegrationDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<IntegrationUser> IntegrationUsers { get; set; }

    public DbSet<IntegrationBlog> IntegrationBlogs => Set<IntegrationBlog>();

    public DbSet<IntegrationOrder> IntegrationOrders => Set<IntegrationOrder>();

    public DbSet<IntegrationOffsetOrder> IntegrationOffsetOrders => Set<IntegrationOffsetOrder>();

    public DbSet<IntegrationOffsetCheckpoint> IntegrationOffsetCheckpoints => Set<IntegrationOffsetCheckpoint>();

    public DbSet<GuidGenerationRow> GuidGenerationRows => Set<GuidGenerationRow>();

    public DbSet<LongGenerationRow> LongGenerationRows => Set<LongGenerationRow>();

    public DbSet<StringGenerationRow> StringGenerationRows => Set<StringGenerationRow>();
}
#pragma warning restore CS8618
