using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.IntegrationTests;

public abstract class AutoTimestampBase
{
    public Guid Id { get; set; }
    [AutoTimestamp]
    public DateTimeOffset CreatedTime { get; set; }
    [AutoTimestamp(onUpdate: true)]
    public DateTimeOffset UpdatedTime { get; set; }
}

public sealed class AutoTimestampItem : AutoTimestampBase
{
    public string Name { get; set; } = "";
}

public sealed class AutoTimestampContext(string connectionString) : DbContext(b => b.UsePostgres(connectionString))
{
    public DbSet<AutoTimestampItem> Items => Set<AutoTimestampItem>();
}

[Collection(PostgresCollection.Name)]
public sealed class AutoTimestampIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Updates_generate_utc_time_and_preserve_explicit_timestamp()
    {
        await using var db = new AutoTimestampContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();
        var item = await db.Items.InsertAsync(new AutoTimestampItem { Name = "before" });
        var before = DateTimeOffset.UtcNow;
        await db.Items.Where(x => x.Id == item.Id).ExecuteUpdateAsync(s => s.Set(x => x.Name, "after"));
        var updated = await db.Items.Where(x => x.Id == item.Id).FirstOrDefaultAsync();
        Assert.NotNull(updated);
        Assert.InRange(updated.UpdatedTime, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(item.CreatedTime, updated.CreatedTime);

        var explicitTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Items.Where(x => x.Id == item.Id).ExecuteUpdateAsync(s => s.Set(x => x.UpdatedTime, explicitTime));
        updated = await db.Items.Where(x => x.Id == item.Id).FirstOrDefaultAsync();
        Assert.Equal(explicitTime, updated!.UpdatedTime);
    }
}
