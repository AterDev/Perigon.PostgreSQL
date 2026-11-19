using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.NativeAotSmoke;

#pragma warning disable CS8618 // Initialized by the DbSet source generator.
public sealed class SmokeDbContext : DbContext
{
    public SmokeDbContext()
    {
    }

    public SmokeDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<SmokeUser> Users { get; set; }
}
#pragma warning restore CS8618
