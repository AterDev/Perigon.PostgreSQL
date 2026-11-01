using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>Registers a pooled context and configures application-wide telemetry registration.</summary>
    public static IServiceCollection AddDbContextPool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure,
        Action<DbContextRegistrationOptions> configureRegistration,
        int poolSize = 128)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configureRegistration);
        var registration = new DbContextRegistrationOptions();
        configureRegistration(registration);
        services.AddDbContextPool<TContext>(configure, poolSize);
        if (registration.EnableOpenTelemetry)
            services.AddPerigonPostgreSqlOpenTelemetry();
        return services;
    }

    /// <summary>Registers a scoped context backed by a bounded pool. Configuration is fixed for the pool lifetime.</summary>
    public static IServiceCollection AddDbContextPool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure,
        int poolSize = 128)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentOutOfRangeException.ThrowIfLessThan(poolSize, 1);

        var constructors = typeof(TContext).GetConstructors();
        if (constructors.Length != 1 || constructors[0].GetParameters() is not [{ ParameterType: var type }] ||
            type != typeof(DbContextOptions<TContext>))
            throw new InvalidOperationException("A pooled context must have exactly one public constructor accepting DbContextOptions<TContext>; scoped dependencies must not be captured by a pooled instance.");

        services.AddSingleton<DataSourceHolder<TContext>>();
        services.AddSingleton<DbContextPool<TContext>>(serviceProvider =>
        {
            var builder = new DbContextOptionsBuilder();
            configure(builder);
            var options = builder.Build<TContext>();
            options.LoggerFactory ??= serviceProvider.GetService<ILoggerFactory>();
            if (options.DataSource is null && !string.IsNullOrWhiteSpace(options.ConnectionString))
                options.DataSource = serviceProvider.GetRequiredService<DataSourceHolder<TContext>>().GetOrCreate(options);
            return new DbContextPool<TContext>(
                () => ActivatorUtilities.CreateInstance<TContext>(serviceProvider, options), poolSize);
        });
        services.AddScoped<PooledContextScope<TContext>>();
        services.AddScoped<TContext>(serviceProvider => serviceProvider.GetRequiredService<PooledContextScope<TContext>>().Context);
        return services;
    }

    public static IServiceCollection AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure,
        Action<DbContextRegistrationOptions> configureRegistration,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddDbContext<TContext>((_, builder) => configure(builder), configureRegistration, lifetime);
    }

    public static IServiceCollection AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure,
        Action<DbContextRegistrationOptions> configureRegistration,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configureRegistration);

        var registration = new DbContextRegistrationOptions();
        configureRegistration(registration);
        if (registration.EnableOpenTelemetry)
            services.AddPerigonPostgreSqlOpenTelemetry();

        return services.AddDbContext<TContext>(configure, lifetime);
    }

    public static IServiceCollection AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddDbContext<TContext>((_, builder) => configure(builder), lifetime);
    }

    public static IServiceCollection AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<DataSourceHolder<TContext>>();

        services.Add(new ServiceDescriptor(
            typeof(DbContextOptions<TContext>),
            serviceProvider =>
            {
                var builder = new DbContextOptionsBuilder();
                configure(serviceProvider, builder);
                var options = builder.Build<TContext>();
                options.LoggerFactory ??= serviceProvider.GetService<ILoggerFactory>();
                if (options.DataSource is null && !string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    options.DataSource = serviceProvider.GetRequiredService<DataSourceHolder<TContext>>().GetOrCreate(options);
                }

                return options;
            },
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(TContext),
            serviceProvider => ActivatorUtilities.CreateInstance<TContext>(
                serviceProvider,
                serviceProvider.GetRequiredService<DbContextOptions<TContext>>()),
            lifetime));

        return services;
    }

    public static IServiceCollection AddDbContext<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TContext>(
        this IServiceCollection services,
        Func<IServiceProvider, TContext> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.Add(new ServiceDescriptor(typeof(TContext), serviceProvider => factory(serviceProvider), lifetime));
        return services;
    }

    private sealed class DataSourceHolder<TContext> : IDisposable
        where TContext : DbContext
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, NpgsqlDataSource> _dataSources = new(StringComparer.Ordinal);

        public NpgsqlDataSource GetOrCreate(DbContextOptions options)
        {
            var connectionString = options.BuildEffectiveConnectionString();
            lock (_sync)
            {
                if (_dataSources.TryGetValue(connectionString, out var existing))
                {
                    return existing;
                }

                var dataSource = options.CreateDataSource();
                _dataSources.Add(connectionString, dataSource);
                return dataSource;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                foreach (var dataSource in _dataSources.Values)
                {
                    dataSource.Dispose();
                }

                _dataSources.Clear();
            }
        }
    }
}
