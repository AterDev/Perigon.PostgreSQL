using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
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

        services.Add(new ServiceDescriptor(
            typeof(DbContextOptions<TContext>),
            serviceProvider =>
            {
                var builder = new DbContextOptionsBuilder();
                configure(serviceProvider, builder);
                return builder.Build<TContext>();
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
}