using Npgsql;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Support;

public sealed class DbContextOptionsBuilderTests
{
    [Fact]
    public void Builder_stores_timeout_and_pooling_options()
    {
        var builder = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres")
            .UseConnectionTimeout(TimeSpan.FromSeconds(7))
            .UseCommandTimeout(TimeSpan.FromSeconds(13))
            .EnableConnectionPooling()
            .UseMinPoolSize(3)
            .UseMaxPoolSize(25);

        var options = builder.Build();

        Assert.Equal(TimeSpan.FromSeconds(7), options.ConnectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(13), options.CommandTimeout);
        Assert.True(options.PoolingEnabled);
        Assert.Equal(3, options.MinPoolSize);
        Assert.Equal(25, options.MaxPoolSize);
    }

    [Fact]
    public void Context_builds_connection_string_with_timeout_and_pooling_settings()
    {
        using var db = new TunedOptionsDbContext(new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres")
            .UseConnectionTimeout(TimeSpan.FromSeconds(9))
            .UseCommandTimeout(TimeSpan.FromSeconds(21))
            .EnableConnectionPooling(false)
            .UseMinPoolSize(0)
            .UseMaxPoolSize(40)
            .Build());

        var connectionString = db.BuildEffectiveConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal(9, builder.Timeout);
        Assert.Equal(21, builder.CommandTimeout);
        Assert.False(builder.Pooling);
        Assert.Equal(0, builder.MinPoolSize);
        Assert.Equal(40, builder.MaxPoolSize);
    }

    [Fact]
    public void Context_applies_command_timeout_to_created_commands()
    {
        using var db = new TunedOptionsDbContext(new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres")
            .UseCommandTimeout(TimeSpan.FromSeconds(12))
            .Build());

        using var command = db.CreateCommand(new BoundSql("select 1", []));

        Assert.Equal(12, command.CommandTimeout);
    }

    [Fact]
    public void Context_normalizes_non_utc_datetimeoffset_parameters_to_utc_datetime()
    {
        using var db = new TunedOptionsDbContext(new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres")
            .Build());
        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));

        using var command = db.CreateCommand(new BoundSql(
            "select $1",
            [new SqlParameterValue(1, start)]));

        var parameterValue = Assert.IsType<DateTime>(command.Parameters[0].Value);
        Assert.Equal(DateTimeKind.Utc, parameterValue.Kind);
        Assert.Equal(start.UtcDateTime, parameterValue);
    }

    [Fact]
    public void External_data_source_is_preserved()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres");
        using var db = new TunedOptionsDbContext(new DbContextOptionsBuilder()
            .UseNpgsql(dataSource)
            .UseCommandTimeout(TimeSpan.FromSeconds(5))
            .EnableConnectionPooling(false)
            .Build());

        Assert.Same(dataSource, db.GetDataSource());
    }

    [Fact]
    public void Context_preserves_npgsql_defaults_when_options_are_not_configured()
    {
        using var db = new TunedOptionsDbContext(new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres")
            .Build());

        var configured = new NpgsqlConnectionStringBuilder(db.BuildEffectiveConnectionString());
        var defaults = new NpgsqlConnectionStringBuilder("Host=localhost;Port=5432;Database=perigon_tests;Username=postgres;Password=postgres");

        Assert.Equal(defaults.Timeout, configured.Timeout);
        Assert.Equal(defaults.CommandTimeout, configured.CommandTimeout);
        Assert.Equal(defaults.Pooling, configured.Pooling);
        Assert.Equal(defaults.MinPoolSize, configured.MinPoolSize);
        Assert.Equal(defaults.MaxPoolSize, configured.MaxPoolSize);
    }

    private sealed class TunedOptionsDbContext : DbContext
    {
        public TunedOptionsDbContext(DbContextOptions options)
            : base(options)
        {
        }

        public DbSet<ConventionUser> ConventionUsers => Set<ConventionUser>();
    }
}