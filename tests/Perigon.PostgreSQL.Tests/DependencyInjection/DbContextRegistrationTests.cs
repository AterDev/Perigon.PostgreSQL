using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.DependencyInjection;

public sealed class DbContextRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DI_logging_redacts_parameters_unless_sensitive_logging_is_enabled(bool sensitive)
    {
        var logger = new RecordingLogger();
        using var loggerFactory = new RecordingLoggerFactory(logger);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddDbContext<OptionsOnlyDbContext>(options => options
            .UseNpgsql("Host=localhost;Database=perigon_tests;Username=postgres")
            .EnableSensitiveLogging(sensitive));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandExecutor.ExecuteNonQueryAsync(
            db, new BoundSql("SELECT $1", [new SqlParameterValue(1, "private-value")]), canceled.Token));

        var executing = Assert.Single(logger.Messages, message => message.Contains("Executing PostgreSQL command"));
        Assert.Contains(sensitive ? "private-value" : "<redacted>", executing);
        if (!sensitive) Assert.DoesNotContain("private-value", executing);
    }

    [Fact]
    public void AddDbContext_uses_DI_logger_factory_and_database_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddDbContext<OptionsOnlyDbContext>(options => options
            .UseNpgsql("Host=localhost;Database=perigon_tests;Username=postgres")
            .UseConnectionTimeout(TimeSpan.FromSeconds(7))
            .UseCommandTimeout(TimeSpan.FromSeconds(13))
            .UseMaxPoolSize(25));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<OptionsOnlyDbContext>>();
        var dataSource = scope.ServiceProvider.GetRequiredService<OptionsOnlyDbContext>().GetDataSource();
        var settings = new Npgsql.NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Same(NullLoggerFactory.Instance, options.LoggerFactory);
        Assert.Equal(7, settings.Timeout);
        Assert.Equal(13, settings.CommandTimeout);
        Assert.Equal(25, settings.MaxPoolSize);
    }

    [Fact]
    public void Explicit_logger_factory_takes_precedence_over_DI()
    {
        var services = new ServiceCollection();
        using var explicitFactory = LoggerFactory.Create(_ => { });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddDbContext<OptionsOnlyDbContext>(options => options.UseLoggerFactory(explicitFactory));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(explicitFactory, scope.ServiceProvider.GetRequiredService<DbContextOptions<OptionsOnlyDbContext>>().LoggerFactory);
    }
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

    private sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
