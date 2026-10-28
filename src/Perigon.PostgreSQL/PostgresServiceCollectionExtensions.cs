using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL;

public static class PostgresServiceCollectionExtensions
{
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
