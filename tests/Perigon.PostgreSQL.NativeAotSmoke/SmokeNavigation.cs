using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokeNavigationContext : DbContext
{
    public SmokeNavigationContext() : base(_ => { }) { }
    public SmokeNavigationContext(string connectionString) : base(options => options.UsePostgres(connectionString)) { }
    public DbSet<SmokeOwner> Owners => Set<SmokeOwner>();
    public DbSet<SmokePost> Posts => Set<SmokePost>();
}

public sealed class SmokeOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [NotMapped] public List<SmokePost> Posts { get; set; } = [];
}

public sealed class SmokePost
{
    public int Id { get; set; }
    public int SmokeOwnerId { get; set; }
    [NotMapped] public SmokeOwner? SmokeOwner { get; set; }
}

public sealed class SmokeAggregateRow
{
    public int Id { get; set; }
    public long Count { get; set; }
    public int Count32 { get; set; }
    public int Sum { get; set; }
    public int? Minimum { get; set; }
    public int? Maximum { get; set; }
    public double? Average { get; set; }
    public int MinimumRequired { get; set; }
}
