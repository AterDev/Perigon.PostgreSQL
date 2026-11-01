namespace Perigon.PostgreSQL.Options;

/// <summary>Options applied when a context is registered in the service collection.</summary>
public sealed class DbContextRegistrationOptions
{
    /// <summary>
    /// Registers Npgsql tracing and metrics with OpenTelemetry for the application. Disabled by default.
    /// Exporters must be configured separately.
    /// </summary>
    public bool EnableOpenTelemetry { get; set; }
}
