using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.Benchmarks;

/// <summary>
/// Compares the production DI registrations for a scoped context and a pooled
/// context without opening a PostgreSQL connection. Each benchmark creates and
/// disposes a real DI scope, resolves the context, and touches one or ten DbSet
/// properties. The model registry is populated with the existing 100/300
/// source-generated entities before either provider is built.
///
/// This benchmark intentionally does not include the old concurrent-queue
/// context-reuse prototype. Pooling is measured only through AddDbContextPool,
/// with its lease validation and reset path.
/// </summary>
[MemoryDiagnoser]
public class DbContextPoolBenchmarks
{
    private static IReadOnlyList<EntityModel>? _allGeneratedModels;
    private NpgsqlDataSource _dataSource = null!;
    private ServiceProvider _scopedProvider = null!;
    private ServiceProvider _pooledProvider = null!;

    [ParamsSource(nameof(ModelSizes))]
    public int ModelSize { get; set; }

    [ParamsSource(nameof(AccessProfiles))]
    public string AccessProfile { get; set; } = "one";

    [ParamsSource(nameof(PoolSizes))]
    public int PoolSize { get; set; }

    public IEnumerable<int> ModelSizes => ReadAllowedIntegers("PERIGON_POOL_MODEL_SIZES", 100, 300, 100, 300);

    public IEnumerable<string> AccessProfiles => ReadAccessProfiles();

    public IEnumerable<int> PoolSizes => ReadAllowedIntegers("PERIGON_POOL_SIZES", 1, 32, 1, 32);

    private int AccessCount => AccessProfile switch
    {
        "one" => 1,
        "ten" => Math.Min(10, ModelSize),
        _ => throw new ArgumentOutOfRangeException(nameof(AccessProfile), AccessProfile, "Use one or ten.")
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        var generated = _allGeneratedModels ??= EntityModelRegistry.GetContextModels(typeof(LargeModelContext));
        if (generated.Count < ModelSize)
        {
            throw new InvalidOperationException(
                $"The source-generated benchmark model contains {generated.Count} entities, expected at least {ModelSize}.");
        }

        for (var i = 0; i < ModelSize; i++)
        {
            if (!generated[i].IsGenerated || generated[i].ClrType.Name != $"LargeModelEntity{i + 1:000}")
                throw new InvalidOperationException("Unexpected generated model ordering or missing generated metadata.");
        }

        // The benchmark context has only the ten properties used by the access
        // profile. Its registry entry still contains the selected 100/300
        // existing source-generated models, so model construction cost is real.
        EntityModelRegistry.RegisterContext<PooledLargeModelContext>(generated.Take(ModelSize).ToArray());

        // NpgsqlDataSource creation is local and does not open a connection.
        // Both registrations use the same stable external data source; neither
        // benchmark executes SQL or requires a running PostgreSQL server.
        _dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=perigon_context_pool_benchmark;Username=postgres;Password=postgres;Pooling=true;Maximum Pool Size=1");
        _scopedProvider = BuildProvider(pooled: false);
        _pooledProvider = BuildProvider(pooled: true);

        // Warm both registrations with the same number of scopes and table
        // accesses. This builds the selected model and exercises the first
        // DbSet lookup before BenchmarkDotNet starts measuring.
        Warmup(_scopedProvider);
        Warmup(_pooledProvider);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _pooledProvider?.Dispose();
        _scopedProvider?.Dispose();
        _dataSource?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "DI scope; AddDbContext")]
    public int ScopedContextPerRequest() => ResolveAndTouch(_scopedProvider);

    [Benchmark(Description = "DI scope; AddDbContextPool")]
    public int PooledContextPerRequest() => ResolveAndTouch(_pooledProvider);

    private int ResolveAndTouch(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PooledLargeModelContext>();
        return context.TouchTables(AccessCount);
    }

    private void Warmup(ServiceProvider provider)
    {
        const int warmupScopes = 2;
        for (var i = 0; i < warmupScopes; i++)
        {
            var actual = ResolveAndTouch(provider);
            if (actual != AccessCount)
                throw new InvalidOperationException($"Expected {AccessCount} DbSet accesses, got {actual}.");
        }
    }

    private ServiceProvider BuildProvider(bool pooled)
    {
        var services = new ServiceCollection();
        if (pooled)
        {
            services.AddDbContextPool<PooledLargeModelContext>(
                options => options.UseNpgsql(_dataSource),
                PoolSize);
        }
        else
        {
            services.AddDbContext<PooledLargeModelContext>(
                options => options.UseNpgsql(_dataSource));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IEnumerable<string> ReadAccessProfiles()
    {
        var raw = Environment.GetEnvironmentVariable("PERIGON_POOL_ACCESSES");
        if (string.IsNullOrWhiteSpace(raw))
            return ["one", "ten"];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .Where(value => value is "one" or "ten")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<int> ReadAllowedIntegers(
        string variable,
        int firstDefault,
        int secondDefault,
        params int[] allowed)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return [firstDefault, secondDefault];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(value => allowed.Contains(value))
            .Distinct()
            .ToArray();
    }
}

/// <summary>
/// Benchmark-only context with one public constructor. The ten expression-
/// bodied properties deliberately avoid eager DbSet creation so the real pool
/// can validate and reset the instance between scopes.
/// </summary>
public sealed class PooledLargeModelContext(DbContextOptions<PooledLargeModelContext> options)
    : DbContext(options)
{
    private static readonly Action<PooledLargeModelContext>[] TableAccessors =
    [
        static context => _ = context.Entity001,
        static context => _ = context.Entity002,
        static context => _ = context.Entity003,
        static context => _ = context.Entity004,
        static context => _ = context.Entity005,
        static context => _ = context.Entity006,
        static context => _ = context.Entity007,
        static context => _ = context.Entity008,
        static context => _ = context.Entity009,
        static context => _ = context.Entity010
    ];

    public DbSet<LargeModelEntity001> Entity001 => Set<LargeModelEntity001>();
    public DbSet<LargeModelEntity002> Entity002 => Set<LargeModelEntity002>();
    public DbSet<LargeModelEntity003> Entity003 => Set<LargeModelEntity003>();
    public DbSet<LargeModelEntity004> Entity004 => Set<LargeModelEntity004>();
    public DbSet<LargeModelEntity005> Entity005 => Set<LargeModelEntity005>();
    public DbSet<LargeModelEntity006> Entity006 => Set<LargeModelEntity006>();
    public DbSet<LargeModelEntity007> Entity007 => Set<LargeModelEntity007>();
    public DbSet<LargeModelEntity008> Entity008 => Set<LargeModelEntity008>();
    public DbSet<LargeModelEntity009> Entity009 => Set<LargeModelEntity009>();
    public DbSet<LargeModelEntity010> Entity010 => Set<LargeModelEntity010>();

    public int TouchTables(int count)
    {
        var actual = Math.Min(count, TableAccessors.Length);
        for (var i = 0; i < actual; i++)
        {
            // Two reads exercise the per-context DbSet cache. The second read
            // should return the same instance within this request scope.
            TableAccessors[i](this);
            TableAccessors[i](this);
        }

        return actual;
    }

}
