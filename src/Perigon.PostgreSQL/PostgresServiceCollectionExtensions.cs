using Microsoft.Extensions.DependencyInjection;

namespace Perigon.PostgreSQL;

public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AddDbContext<TContext>(
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