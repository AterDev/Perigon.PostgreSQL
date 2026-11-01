using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.ApiBenchmarks;

internal static class ApiBenchmarkSettings
{
    public static bool UseFluent { get; set; }
}

internal sealed record BenchmarkSettings(
    string ConnectionString,
    int[] ModelSizes,
    bool[] FluentValues,
    string[] Modes,
    int RequestCount,
    int WarmupRequests,
    int Concurrency,
    int TenantCount,
    int PoolSize,
    int NpgsqlPoolSize)
{
    public static BenchmarkSettings Read()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PERIGON_API_BENCHMARK_POSTGRES_CONNECTION_STRING")
            ?? "Host=127.0.0.1;Port=5432;Database=perigon_benchmark;Username=postgres;Password=postgres";

        return new BenchmarkSettings(
            connectionString,
            ReadIntegers("PERIGON_API_MODEL_SIZES", 100, 300),
            ReadBooleans("PERIGON_API_FLUENT_VALUES", false, true),
            ReadModes(),
            ReadPositiveInt("PERIGON_API_REQUESTS", 256),
            ReadPositiveInt("PERIGON_API_WARMUP", 32),
            ReadPositiveInt("PERIGON_API_CONCURRENCY", 32),
            ReadPositiveInt("PERIGON_API_TENANTS", 8),
            ReadNonNegativeInt("PERIGON_API_CONTEXT_POOL_SIZE", 0),
            ReadPositiveInt("PERIGON_API_NPGSQL_POOL_SIZE", 128));
    }

    private static int[] ReadIntegers(string name, params int[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .Where(value => value is 100 or 300)
            .Distinct()
            .ToArray();
        return values.Length == 0 ? defaults : values;
    }

    private static bool[] ReadBooleans(string name, params bool[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => bool.Parse(value))
            .Distinct()
            .ToArray();
        return values.Length == 0 ? defaults : values;
    }

    private static string[] ReadModes()
    {
        var raw = Environment.GetEnvironmentVariable("PERIGON_API_BENCHMARK_MODES");
        if (string.IsNullOrWhiteSpace(raw))
            return ["scoped", "pooled"];

        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .Where(value => value is "scoped" or "pooled")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? ["scoped", "pooled"] : values;
    }

    private static int ReadPositiveInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var value = int.Parse(raw, CultureInfo.InvariantCulture);
        if (value < 1)
            throw new InvalidOperationException($"{name} must be greater than zero.");
        return value;
    }

    private static int ReadNonNegativeInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var value = int.Parse(raw, CultureInfo.InvariantCulture);
        if (value < 0)
            throw new InvalidOperationException($"{name} cannot be negative.");
        return value;
    }
}

internal sealed record ApiResponse(int Tenant, int Count, string? Name, int InitialTenant);
internal sealed record StateResponse(int TenantId);

internal sealed record BenchmarkResult(
    string Mode,
    int ModelSize,
    bool Fluent,
    int Requests,
    int WarmupRequests,
    int Concurrency,
    int TenantCount,
    int ContextPoolSize,
    double ElapsedMilliseconds,
    double RequestsPerSecond,
    double AverageMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    int Errors,
    int TenantIsolationErrors,
    int StateLeakErrors)
{
    public void Print()
    {
        Console.WriteLine(
            $"RESULT mode={Mode} model={ModelSize} fluent={Fluent} requests={Requests} " +
            $"concurrency={Concurrency} throughput={RequestsPerSecond:F2}/s " +
            $"avg={AverageMilliseconds:F3}ms p95={P95Milliseconds:F3}ms p99={P99Milliseconds:F3}ms " +
            $"errors={Errors} tenantErrors={TenantIsolationErrors} stateLeaks={StateLeakErrors}");
        Console.WriteLine(JsonSerializer.Serialize(this));
    }
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main()
    {
        var settings = BenchmarkSettings.Read();
        ValidateSettings(settings);

        var schema = "api_bench_" + Guid.NewGuid().ToString("N")[..12];
        NpgsqlDataSource? dataSource = null;
        var schemaCreated = false;

        try
        {
            await PrepareDatabaseAsync(settings.ConnectionString, schema, settings.TenantCount,
                () => schemaCreated = true);

            var benchmarkConnectionStringBuilder = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
            {
                SearchPath = schema,
                Pooling = true,
                MaxPoolSize = settings.NpgsqlPoolSize
            };
            dataSource = NpgsqlDataSource.Create(benchmarkConnectionStringBuilder.ConnectionString);

            var generatedModels = EntityModelRegistry.GetContextModels(typeof(ApiDbContext));
            ValidateGeneratedModels(generatedModels);
            Console.WriteLine(
                $"PostgreSQL API benchmark schema={schema}; model options={string.Join(",", settings.ModelSizes)}; " +
                $"fluent options={string.Join(",", settings.FluentValues)}; modes={string.Join(",", settings.Modes)}");

            var failed = false;
            foreach (var mode in settings.Modes)
            {
                foreach (var modelSize in settings.ModelSizes)
                {
                    foreach (var fluent in settings.FluentValues)
                    {
                        ApiBenchmarkSettings.UseFluent = fluent;
                        var selectedModels = SelectModels(generatedModels, modelSize);
                        EntityModelRegistry.RegisterContext<ApiDbContext>(selectedModels);

                        var poolSize = settings.PoolSize == 0
                            ? Math.Max(settings.Concurrency, 16)
                            : settings.PoolSize;
                        var app = BuildApplication(dataSource, mode == "pooled", poolSize);
                        try
                        {
                            await app.StartAsync();
                            var baseAddress = GetServerAddress(app);
                            var result = await RunScenarioAsync(
                                baseAddress,
                                mode,
                                modelSize,
                                fluent,
                                settings,
                                poolSize);
                            result.Print();
                            failed |= result.Errors != 0 ||
                                      result.TenantIsolationErrors != 0 ||
                                      result.StateLeakErrors != 0;
                        }
                        finally
                        {
                            try
                            {
                                await app.StopAsync();
                            }
                            finally
                            {
                                await app.DisposeAsync();
                            }
                        }
                    }
                }
            }

            return failed ? 1 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"BENCHMARK ERROR: {exception}");
            return 1;
        }
        finally
        {
            if (dataSource is not null)
                await dataSource.DisposeAsync();

            try
            {
                if (schemaCreated)
                    await DropSchemaAsync(settings.ConnectionString, schema);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Cleanup warning: failed to drop schema {schema}: {exception.Message}");
            }
        }
    }

    private static void ValidateSettings(BenchmarkSettings settings)
    {
        if (settings.ModelSizes.Length == 0)
            throw new InvalidOperationException("At least one model size (100 or 300) is required.");
        if (settings.FluentValues.Length == 0)
            throw new InvalidOperationException("At least one Fluent setting is required.");
        if (settings.Modes.Length == 0)
            throw new InvalidOperationException("At least one mode (scoped or pooled) is required.");
        if (settings.TenantCount < 2)
            throw new InvalidOperationException("At least two tenants are required for isolation validation.");
    }

    private static void ValidateGeneratedModels(IReadOnlyList<EntityModel> generatedModels)
    {
        if (generatedModels.Count < 300)
        {
            throw new InvalidOperationException(
                $"The source-generated API fixture contains {generatedModels.Count} models; expected at least 300.");
        }

        var ordered = generatedModels
            .OrderBy(model => model.ClrType.Name, StringComparer.Ordinal)
            .ToArray();
        for (var i = 1; i <= 300; i++)
        {
            var expected = $"ApiEntity{i:000}";
            if (ordered[i - 1].ClrType.Name != expected)
                throw new InvalidOperationException(
                    $"Unexpected generated model ordering at {i}: {ordered[i - 1].ClrType.Name}, expected {expected}.");
        }
    }

    private static IReadOnlyList<EntityModel> SelectModels(
        IReadOnlyList<EntityModel> generatedModels,
        int modelSize)
    {
        var selected = generatedModels
            .Where(model => model.ClrType.Name.StartsWith("ApiEntity", StringComparison.Ordinal))
            .OrderBy(model => model.ClrType.Name, StringComparer.Ordinal)
            .Take(modelSize)
            .ToArray();

        if (selected.Length != modelSize ||
            selected.All(model => model.ClrType != typeof(ApiEntity001)))
        {
            throw new InvalidOperationException($"Could not select a {modelSize}-entity generated model.");
        }

        return selected;
    }

    private static WebApplication BuildApplication(
        NpgsqlDataSource dataSource,
        bool pooled,
        int contextPoolSize)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            EnvironmentName = "Production",
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        if (pooled)
        {
            builder.Services.AddDbContextPool<ApiDbContext>(
                options => options.UseNpgsql(dataSource),
                contextPoolSize);
        }
        else
        {
            builder.Services.AddDbContext<ApiDbContext>(
                options => options.UseNpgsql(dataSource));
        }

        var app = builder.Build();
        app.MapGet(
            "/rows/{tenant:int}",
            async (int tenant, ApiDbContext context, CancellationToken cancellationToken) =>
            {
                var initialTenant = context.TenantId;
                context.TenantId = tenant;
                var rows = await context.Entity001
                    .OrderBy(row => row.Id)
                    .ToListAsync(cancellationToken);
                var row = rows.Count == 1 ? rows[0] : null;
                return Results.Json(
                    new ApiResponse(tenant, rows.Count, row?.Name, initialTenant),
                    JsonOptions);
            });
        app.MapGet(
            "/state",
            (ApiDbContext context) => Results.Json(new StateResponse(context.TenantId), JsonOptions));
        app.MapGet("/health", () => Results.Ok("ok"));
        return app;
    }

    private static Uri GetServerAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not expose a listening address.");
        return new Uri(address);
    }

    private static async Task<BenchmarkResult> RunScenarioAsync(
        Uri baseAddress,
        string mode,
        int modelSize,
        bool fluent,
        BenchmarkSettings settings,
        int contextPoolSize)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

        await WarmupAndValidateAsync(client, settings, fluent);
        var stateLeakErrors = await ValidateResetStateAsync(client, settings);

        var durations = new ConcurrentBag<double>();
        var errors = 0;
        var tenantIsolationErrors = 0;
        var nextRequest = -1;
        var stopwatch = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, settings.Concurrency).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextRequest);
                if (index >= settings.RequestCount)
                    return;

                var tenant = index % settings.TenantCount + 1;
                var requestTimer = Stopwatch.StartNew();
                try
                {
                    using var response = await client.GetAsync(
                        $"/rows/{tenant}",
                        HttpCompletionOption.ResponseHeadersRead);
                    var payload = response.IsSuccessStatusCode
                        ? await response.Content.ReadFromJsonAsync<ApiResponse>(JsonOptions)
                        : null;
                    requestTimer.Stop();
                    durations.Add(requestTimer.Elapsed.TotalMilliseconds);

                    if (!response.IsSuccessStatusCode || payload is null)
                    {
                        Interlocked.Increment(ref errors);
                        return;
                    }

                    if (payload.Tenant != tenant ||
                        payload.Count != 1 ||
                        payload.Name != ExpectedName(tenant, fluent))
                    {
                        Interlocked.Increment(ref tenantIsolationErrors);
                        Interlocked.Increment(ref errors);
                    }
                    if (payload.InitialTenant != 0)
                    {
                        Interlocked.Increment(ref stateLeakErrors);
                        Interlocked.Increment(ref errors);
                    }
                }
                catch (Exception exception)
                {
                    requestTimer.Stop();
                    durations.Add(requestTimer.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref errors);
                    Console.Error.WriteLine($"request {index} tenant {tenant} failed: {exception.Message}");
                }
            }
        }).ToArray();

        await Task.WhenAll(workers);
        stopwatch.Stop();

        if (durations.Count != settings.RequestCount)
        {
            var missing = settings.RequestCount - durations.Count;
            Interlocked.Add(ref errors, missing);
        }

        var ordered = durations.OrderBy(value => value).ToArray();
        return new BenchmarkResult(
            mode,
            modelSize,
            fluent,
            settings.RequestCount,
            settings.WarmupRequests,
            settings.Concurrency,
            settings.TenantCount,
            contextPoolSize,
            stopwatch.Elapsed.TotalMilliseconds,
            settings.RequestCount / stopwatch.Elapsed.TotalSeconds,
            ordered.Length == 0 ? 0 : ordered.Average(),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            errors,
            tenantIsolationErrors,
            stateLeakErrors);
    }

    private static async Task WarmupAndValidateAsync(
        HttpClient client,
        BenchmarkSettings settings,
        bool fluent)
    {
        var count = Math.Max(settings.WarmupRequests, settings.TenantCount * 2);
        var workers = Enumerable.Range(0, settings.Concurrency).Select(async worker =>
        {
            for (var i = worker; i < count; i += settings.Concurrency)
            {
                var tenant = i % settings.TenantCount + 1;
                var payload = await GetJsonAsync<ApiResponse>(client, $"/rows/{tenant}");
                if (payload.Tenant != tenant || payload.Count != 1 ||
                    payload.Name != ExpectedName(tenant, fluent) || payload.InitialTenant != 0)
                    throw new InvalidOperationException(
                        $"Warmup tenant validation failed: expected tenant {tenant} and clean state, got {payload}.");
            }
        });
        await Task.WhenAll(workers);
    }

    private static async Task<int> ValidateResetStateAsync(
        HttpClient client,
        BenchmarkSettings settings)
    {
        var failures = 0;
        var probes = Math.Max(4, settings.TenantCount);
        for (var i = 0; i < probes; i++)
        {
            var tenant = i % settings.TenantCount + 1;
            _ = await GetJsonAsync<ApiResponse>(client, $"/rows/{tenant}");
            var state = await GetJsonAsync<StateResponse>(client, "/state");
            if (state.TenantId != 0)
            {
                failures++;
                Console.Error.WriteLine(
                    $"STATE LEAK: after tenant {tenant}, the next context reported TenantId={state.TenantId}.");
            }
        }

        return failures;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Empty JSON response from {path}.");
    }

    private static string ExpectedName(int tenant, bool fluent)
        => fluent ? $"fluent-tenant-{tenant}" : $"tenant-{tenant}";

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(values.Length * percentile) - 1;
        index = Math.Clamp(index, 0, values.Length - 1);
        return values[index];
    }

    private static async Task PrepareDatabaseAsync(
        string connectionString,
        string schema,
        int tenantCount,
        Action onSchemaCreated)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        var quotedSchema = QuoteIdentifier(schema);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA {quotedSchema};";
            await command.ExecuteNonQueryAsync();
            onSchemaCreated();
            command.CommandText = $"""
                CREATE TABLE {quotedSchema}."api_entity001s" (
                    "id" integer PRIMARY KEY,
                    "tenant_id" integer NOT NULL,
                    "name" text NOT NULL
                );
                CREATE TABLE {quotedSchema}."api_rows" (
                    "id" integer PRIMARY KEY,
                    "tenant_id" integer NOT NULL,
                    "name" text NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO {quotedSchema}.\"api_entity001s\" (\"id\", \"tenant_id\", \"name\") VALUES (@id, @tenant, @name);" +
            $" INSERT INTO {quotedSchema}.\"api_rows\" (\"id\", \"tenant_id\", \"name\") VALUES (@id, @tenant, @fluentName);";
        insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Integer));
        insert.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Integer));
        insert.Parameters.Add(new NpgsqlParameter("name", NpgsqlTypes.NpgsqlDbType.Text));
        insert.Parameters.Add(new NpgsqlParameter("fluentName", NpgsqlTypes.NpgsqlDbType.Text));
        for (var tenant = 1; tenant <= tenantCount; tenant++)
        {
            insert.Parameters[0].Value = tenant;
            insert.Parameters[1].Value = tenant;
            insert.Parameters[2].Value = $"tenant-{tenant}";
            insert.Parameters[3].Value = $"fluent-tenant-{tenant}";
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {QuoteIdentifier(schema)} CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
