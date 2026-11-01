using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.RawSql;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class OpenTelemetryIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Pooled_context_with_repeated_registration_emits_one_command_activity()
    {
        var exported = new ActivityExporter();
        var services = new ServiceCollection();
        services.AddPerigonPostgreSqlOpenTelemetry();
        services.AddDbContext<TelemetryContext>(options => options.UseNpgsql(fixture.ConnectionString),
            registration => registration.EnableOpenTelemetry = true);
        services.AddDbContextPool<PooledTelemetryContext>(options => options.UseNpgsql(fixture.ConnectionString),
            registration => registration.EnableOpenTelemetry = true);
        services.AddOpenTelemetry().WithTracing(builder =>
            builder.AddProcessor(new SimpleActivityExportProcessor(exported)));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PooledTelemetryContext>();

        await db.SqlCommand($"SELECT 1").ExecuteAsync();

        Assert.Single(exported.Activities, activity =>
            activity.Source.Name == "Npgsql" && activity.Kind == ActivityKind.Client &&
            !activity.DisplayName.StartsWith("CONNECT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Database_command_emits_Npgsql_activity()
    {
        var exported = new ActivityExporter();
        var metrics = new MetricExporter();
        var services = new ServiceCollection();
        services.AddDbContext<TelemetryContext>(options => options.UseNpgsql(fixture.ConnectionString),
            registration => registration.EnableOpenTelemetry = true);
        services.AddOpenTelemetry().WithTracing(builder =>
            builder.AddProcessor(new SimpleActivityExportProcessor(exported)))
            .WithMetrics(builder => builder.AddReader(new BaseExportingMetricReader(metrics)));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryContext>();

        await db.SqlCommand($"SELECT 1").ExecuteAsync();

        Assert.Contains(exported.Activities, activity =>
            activity.Source.Name == "Npgsql" && activity.Kind == ActivityKind.Client);
        Assert.True(meterProvider.ForceFlush());
        Assert.Contains("db.client.operation.duration", metrics.Names);
    }

    [Fact]
    public async Task Registered_command_timeout_is_enforced()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TelemetryContext>(options => options.UseNpgsql(fixture.ConnectionString)
            .UseCommandTimeout(TimeSpan.FromSeconds(1)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryContext>();

        var exception = await Assert.ThrowsAsync<Npgsql.NpgsqlException>(() =>
            db.SqlCommand($"SELECT pg_sleep(3)").ExecuteAsync());
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    private sealed class TelemetryContext(DbContextOptions<TelemetryContext> options) : DbContext(options);

    public sealed class PooledTelemetryContext(DbContextOptions<PooledTelemetryContext> options) : DbContext(options);

    private sealed class ActivityExporter : BaseExporter<Activity>
    {
        public List<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Activities.Add(activity);
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
