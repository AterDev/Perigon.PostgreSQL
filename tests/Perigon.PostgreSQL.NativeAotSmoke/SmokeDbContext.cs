using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokeDbContext : DbContext
{
    public DbSet<SmokeUser> Users => Set<SmokeUser>();
}
