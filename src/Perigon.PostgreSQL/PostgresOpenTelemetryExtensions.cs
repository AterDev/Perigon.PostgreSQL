using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Perigon.PostgreSQL;

/// <summary>Registers Npgsql tracing and metrics with the application's OpenTelemetry providers.</summary>
public static class PostgresOpenTelemetryExtensions
{
    /// <summary>
    /// Enables Npgsql tracing and metrics. Configure exporters separately on the host's OpenTelemetry providers.
    /// </summary>
    public static IServiceCollection AddPerigonPostgreSqlOpenTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistrationMarker)))
            return services;

        services.AddSingleton(new RegistrationMarker());

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddNpgsql())
            .WithMetrics(metrics => metrics.AddMeter("Npgsql"));

        return services;
    }

    private sealed class RegistrationMarker;
}
