using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokeDbContext : PostgresDbContext
{
    public DbSet<SmokeUser> Users => Set<SmokeUser>();
}
