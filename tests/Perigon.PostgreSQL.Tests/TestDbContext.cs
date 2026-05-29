using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests;

public sealed class TestDbContext : DbContext
{
    public DbSet<ConventionUser> ConventionUsers => Set<ConventionUser>();

    public DbSet<AttributedUser> AttributedUsers => Set<AttributedUser>();

    public DbSet<RichUser> RichUsers => Set<RichUser>();

    public DbSet<StatisticOrder> StatisticOrders => Set<StatisticOrder>();

    public DbSet<StatisticOffsetOrder> StatisticOffsetOrders => Set<StatisticOffsetOrder>();

    public DbSet<StatisticOffsetCheckpoint> StatisticOffsetCheckpoints => Set<StatisticOffsetCheckpoint>();

    public DbSet<Blog> Blogs => Set<Blog>();
}
