using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Tests.Sql;

public abstract class AuditedRow
{
    public Guid Id { get; set; }
    [AutoTimestamp]
    public DateTimeOffset CreatedTime { get; set; }
    [AutoTimestamp(onUpdate: true)]
    public DateTimeOffset UpdatedTime { get; set; }
}

public sealed class AuditedItem : AuditedRow
{
    public string Name { get; set; } = "";
}

public sealed class AuditedContext : DbContext
{
    public DbSet<AuditedItem> Items => Set<AuditedItem>();
    public DbSet<AuditedRow> BaseRows => Set<AuditedRow>();
}

public sealed class AutoTimestampTests
{
    [Fact]
    public void Abstract_base_is_not_registered_as_an_entity()
    {
        var models = EntityModelRegistry.GetContextModels(typeof(AuditedContext));
        Assert.Contains(models, model => model.ClrType == typeof(AuditedItem));
        Assert.DoesNotContain(models, model => model.ClrType == typeof(AuditedRow));
        using var db = new AuditedContext();
        Assert.Throws<InvalidOperationException>(() => db.BaseRows);
    }

    [Fact]
    public void Inherited_timestamp_configuration_is_used_by_update()
    {
        using var db = new AuditedContext();
        var before = DateTimeOffset.UtcNow;
        var sql = db.Items.Where(x => x.Id == Guid.Empty).ToUpdateSql(s => s.Set(x => x.Name, "new"));
        Assert.Contains("\"updated_time\" = $2", sql.CommandText);
        Assert.DoesNotContain("\"created_time\" =", sql.CommandText);
        var timestamp = Assert.IsType<DateTimeOffset>(sql.Parameters[1].Value);
        Assert.InRange(timestamp, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Explicit_update_timestamp_wins()
    {
        using var db = new AuditedContext();
        var value = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var sql = db.Items.Where(x => x.Id == Guid.Empty)
            .ToUpdateSql(s => s.Set(x => x.UpdatedTime, value));
        Assert.Equal(1, sql.CommandText.Split("\"updated_time\" =").Length - 1);
        Assert.Equal(value, sql.Parameters[0].Value);
    }
}
