using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests;

public sealed class TestDbContext : PostgresDbContext
{
    public DbSet<ConventionUser> ConventionUsers => Set<ConventionUser>();

    public DbSet<AttributedUser> AttributedUsers => Set<AttributedUser>();

    public DbSet<RichUser> RichUsers => Set<RichUser>();

    public DbSet<Blog> Blogs => Set<Blog>();
}
