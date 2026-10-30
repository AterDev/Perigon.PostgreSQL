using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public abstract class SmokeAuditBase
{
    public Guid Id { get; set; }
    [AutoTimestamp(onUpdate: true)]
    public DateTimeOffset UpdatedTime { get; set; }
}

public sealed class SmokeAuditItem : SmokeAuditBase
{
    public string Name { get; set; } = "";
}

public sealed class SmokeAuditContext : DbContext
{
    public string ExcludedName { get; set; } = "hidden";
    public DbSet<SmokeAuditItem> Items => Set<SmokeAuditItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => HasQueryFilter<SmokeAuditItem>("Visible", item => item.Name != ExcludedName);
}
