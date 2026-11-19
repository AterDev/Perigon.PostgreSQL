using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokePooledContext(DbContextOptions<SmokePooledContext> options) : DbContext(options)
{
    public int MinimumAge { get; set; }
#pragma warning disable CS8618 // Initialized by the DbSet source generator.
    public DbSet<SmokeUser> Users { get; set; }
#pragma warning restore CS8618
    protected override void OnModelCreating(ModelBuilder builder)
        => HasQueryFilter<SmokeUser>("MinimumAge", user => user.Age >= MinimumAge);
    protected override void OnPoolReturn() => MinimumAge = 0;
}
