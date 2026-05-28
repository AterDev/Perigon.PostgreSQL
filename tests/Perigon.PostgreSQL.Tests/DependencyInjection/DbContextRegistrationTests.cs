using Microsoft.Extensions.DependencyInjection;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.DependencyInjection;

public sealed class DbContextRegistrationTests
{
    [Fact]
    public void AddDbContext_options_overload_supports_options_constructor()
    {
        var services = new ServiceCollection();

        services.AddDbContext<OptionsOnlyDbContext>(options =>
            options.UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<OptionsOnlyDbContext>>();

        Assert.NotNull(db);
        Assert.NotNull(options);
        Assert.NotNull(db.GetDataSource());
    }

    private sealed class OptionsOnlyDbContext : DbContext
    {
        public OptionsOnlyDbContext(DbContextOptions<OptionsOnlyDbContext> options)
            : base(options)
        {
        }

        public DbSet<ConventionUser> ConventionUsers => Set<ConventionUser>();
    }
}