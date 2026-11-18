using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public abstract class SmokeAuditBase
{
    public Guid Id { get; set; }
    [Column("is_archived")]
    public bool IsDeleted { get; set; }
    [AutoTimestamp(onUpdate: true)]
    public DateTimeOffset UpdatedTime { get; set; }
}

[Index(nameof(Name), IsUnique = true)]
[Index(nameof(ScopeCode), IsUnique = true, Filter = "\"is_current\" = true")]
[Index(nameof(IsCurrent))]
public sealed class SmokeAuditItem : SmokeAuditBase
{
    public string Name { get; set; } = "";
    public string ScopeCode { get; set; } = "scope";
    public bool IsCurrent { get; set; }
}

public sealed class SmokeAuditContext : DbContext
{
    public SmokeAuditContext()
    {
    }

    public SmokeAuditContext(string connectionString)
        : base(options => options.UseNpgsql(connectionString))
    {
    }

    public string ExcludedName { get; set; } = "hidden";
    public DbSet<SmokeAuditItem> Items => Set<SmokeAuditItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        HasQueryFilter<SmokeAuditItem>("Visible", item => item.Name != ExcludedName);
        EnableSoftDeleteFilter<SmokeAuditBase>("SoftDelete", item => !item.IsDeleted);
    }
}
