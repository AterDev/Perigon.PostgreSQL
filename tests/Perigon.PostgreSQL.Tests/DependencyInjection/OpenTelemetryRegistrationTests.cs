using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Perigon.PostgreSQL.Tests.DependencyInjection;

public sealed class OpenTelemetryRegistrationTests
{
    [Fact]
    public void Repeated_and_mixed_registrations_add_Npgsql_instrumentation_once()
    {
        var services = new ServiceCollection();
        services.AddPerigonPostgreSqlOpenTelemetry();
        var telemetryCount = services.Count;
        var telemetryDescriptors = services.GroupBy(descriptor => descriptor.ServiceType)
            .ToDictionary(group => group.Key, group => group.Count());
        services.AddPerigonPostgreSqlOpenTelemetry();
        Assert.Equal(telemetryCount, services.Count);

        services.AddDbContext<TelemetryContext>(_ => { }, registration => registration.EnableOpenTelemetry = true);
        services.AddDbContextPool<PooledTelemetryContext>(_ => { }, registration => registration.EnableOpenTelemetry = true);
        foreach (var (serviceType, count) in telemetryDescriptors)
            Assert.Equal(count, services.Count(descriptor => descriptor.ServiceType == serviceType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_enables_Npgsql_trace_and_metric_sources(bool viaDbContext)
    {
        var services = new ServiceCollection();
        var traces = new TraceExporter();
        var metrics = new MetricExporter();
        if (viaDbContext)
            services.AddDbContext<TelemetryContext>(_ => { }, registration => registration.EnableOpenTelemetry = true);
        else
            services.AddPerigonPostgreSqlOpenTelemetry();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddProcessor(new SimpleActivityExportProcessor(traces)))
            .WithMetrics(builder => builder.AddReader(new BaseExportingMetricReader(metrics)));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        using var source = new ActivitySource("Npgsql");
        using var meter = new Meter("Npgsql");
        var counter = meter.CreateCounter<long>("perigon.test.commands");

        using (var activity = source.StartActivity("perigon.test.command"))
        {
            Assert.NotNull(activity);
            counter.Add(1);
        }

        Assert.True(meterProvider.ForceFlush());
        Assert.Contains("perigon.test.command", traces.Names);
        Assert.Contains("perigon.test.commands", metrics.Names);
    }

    [Fact]
    public void DbContext_registration_does_not_enable_telemetry_by_default()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TelemetryContext>(_ => { }, _ => { });
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void Provider_aware_registration_can_enable_telemetry()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TelemetryContext>((_, options) => { },
            registration => registration.EnableOpenTelemetry = true);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TracerProvider>());
        Assert.NotNull(provider.GetRequiredService<MeterProvider>());
    }

    [Fact]
    public void Pooled_context_registration_can_enable_telemetry()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PooledTelemetryContext>(_ => { },
            registration => registration.EnableOpenTelemetry = true);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TracerProvider>());
        Assert.NotNull(provider.GetRequiredService<MeterProvider>());
    }

    private sealed class TelemetryContext : DbContext
    {
        public TelemetryContext(Options.DbContextOptions<TelemetryContext> options) : base(options) { }
    }

    public sealed class PooledTelemetryContext(Options.DbContextOptions<PooledTelemetryContext> options)
        : DbContext(options), IPooledDbContext
    {
        public void ResetState() { }
    }

    private sealed class TraceExporter : BaseExporter<Activity>
    {
        public List<string> Names { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Names.Add(activity.DisplayName);
            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        public List<string> Names { get; } = [];
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch) Names.Add(metric.Name);
            return ExportResult.Success;
        }
    }
}
