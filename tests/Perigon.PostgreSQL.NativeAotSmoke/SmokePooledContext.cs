using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokePooledContext(DbContextOptions<SmokePooledContext> options) : DbContext(options)
{
    public int MinimumAge { get; set; }
    public DbSet<SmokeUser> Users => Set<SmokeUser>();
    protected override void OnModelCreating(ModelBuilder builder)
        => HasQueryFilter<SmokeUser>("MinimumAge", user => user.Age >= MinimumAge);
    protected override void OnPoolReturn() => MinimumAge = 0;
}
