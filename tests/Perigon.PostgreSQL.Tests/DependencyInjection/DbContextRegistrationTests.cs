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

    [Fact]
    public void AddDbContext_reuses_one_data_source_across_scopes()
    {
        var services = new ServiceCollection();

        services.AddDbContext<OptionsOnlyDbContext>(options =>
            options.UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres"));

        using var provider = services.BuildServiceProvider();
        Npgsql.NpgsqlDataSource first;
        Npgsql.NpgsqlDataSource second;
        using (var firstScope = provider.CreateScope())
        {
            first = firstScope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>().GetDataSource();
        }

        using (var secondScope = provider.CreateScope())
        {
            second = secondScope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>().GetDataSource();
        }

        Assert.Same(first, second);
    }

    [Fact]
    public void AddDbContext_uses_distinct_data_sources_for_distinct_connection_strings()
    {
        var connectionString = "Host=localhost;Port=5432;Database=perigon_tenant_a;Username=postgres;Password=postgres";
        var services = new ServiceCollection();
        services.AddDbContext<OptionsOnlyDbContext>(_ => _.UseNpgsql(connectionString));

        using var provider = services.BuildServiceProvider();
        Npgsql.NpgsqlDataSource first;
        using (var firstScope = provider.CreateScope())
        {
            first = firstScope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>().GetDataSource();
        }

        connectionString = "Host=localhost;Port=5432;Database=perigon_tenant_b;Username=postgres;Password=postgres";
        using (var secondScope = provider.CreateScope())
        {
            var second = secondScope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>().GetDataSource();
            Assert.NotSame(first, second);
        }
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
