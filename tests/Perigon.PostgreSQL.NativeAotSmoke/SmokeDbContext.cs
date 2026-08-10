using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokeDbContext : DbContext
{
    public SmokeDbContext()
    {
    }

    public SmokeDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<SmokeUser> Users => Set<SmokeUser>();
}
