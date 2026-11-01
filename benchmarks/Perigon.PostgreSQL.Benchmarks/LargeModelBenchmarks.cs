using BenchmarkDotNet.Attributes;
using System.Collections.Concurrent;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Benchmarks;

/// <summary>
/// Measures model lookup and context lifetime costs without opening a database connection.
/// The checked-in report is historical: it was captured before the production
/// DbContext gained its instance-level model cache. The ordinary path now also
/// caches resolved models (and DbSet instances within a context), so the old
/// cache flag is retained only as a legacy comparison control. This fixture is
/// not evidence that the current path has no cache.
/// Select combinations with PERIGON_MODEL_CACHE_SIZES=100,300,
/// PERIGON_MODEL_CACHE_ACCESSES=one,ten,all, and PERIGON_MODEL_CACHE_FLUENT=false,true.
/// </summary>
[MemoryDiagnoser]
public class LargeModelBenchmarks
{
    private static IReadOnlyList<EntityModel>? _allGeneratedModels;
    private NpgsqlDataSource _dataSource = null!;
    private LargeModelContext _warmContext = null!;
    private readonly ConcurrentQueue<LargeModelContext> _unsafePrototypePool = new();

    [ParamsSource(nameof(ModelSizes))]
    public int ModelSize { get; set; }

    [ParamsSource(nameof(AccessProfiles))]
    public string AccessProfile { get; set; } = "one";

    [ParamsSource(nameof(FluentValues))]
    public bool Fluent { get; set; }

    public IEnumerable<int> ModelSizes => ReadIntValues("PERIGON_MODEL_CACHE_SIZES", 100, 300);

    public IEnumerable<string> AccessProfiles => ReadStringValues(
        "PERIGON_MODEL_CACHE_ACCESSES",
        "one",
        "ten",
        "all");

    public IEnumerable<bool> FluentValues => ReadBoolValues("PERIGON_MODEL_CACHE_FLUENT", false, true);

    private int AccessCount => AccessProfile switch
    {
        "one" => 1,
        "ten" => Math.Min(10, ModelSize),
        "all" => ModelSize,
        _ => throw new ArgumentOutOfRangeException(nameof(AccessProfile), AccessProfile, "Use one, ten, or all.")
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        LargeModelBenchmarkSettings.ModelSize = ModelSize;
        LargeModelBenchmarkSettings.UseFluent = Fluent;
        _dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=perigon_model_cache_benchmark;Username=postgres;Password=postgres");

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

        EntityModelRegistry.RegisterContext<LargeModelContext>(generated.Take(ModelSize).ToArray());
        // Historical ideal context-reuse upper bound, not the production pool.
        _warmContext = new LargeModelContext(_dataSource, cacheModels: true);
        _ = _warmContext.TouchSets(AccessCount);
        using (var baseline = new LargeModelContext(_dataSource))
        {
            var expected = baseline.Entity001.Model;
            var actual = _warmContext.Entity001.Model;
            if (actual.ClrType != expected.ClrType || actual.TableName != expected.TableName ||
                actual.Comment != expected.Comment || actual.Columns.Count != expected.Columns.Count ||
                (Fluent && actual.Comment != "model-cache-benchmark"))
                throw new InvalidOperationException("The model-cache candidate differs from the current model path.");
        }
        _unsafePrototypePool.Enqueue(_warmContext);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _warmContext?.Dispose();
        _dataSource?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "New context; current model path")]
    public int NewContextCurrentModelPath()
    {
        using var context = new LargeModelContext(_dataSource);
        return context.TouchSets(AccessCount);
    }

    [Benchmark(Description = "New context; legacy cache flag (historical control)")]
    public int NewContextCachedModelPath()
    {
        using var context = new LargeModelContext(_dataSource, cacheModels: true);
        return context.TouchSets(AccessCount);
    }

    [Benchmark(Description = "Reuse one context; ideal upper bound")]
    public int ReuseOneContextUpperBound()
    {
        return _warmContext.TouchSets(AccessCount);
    }

    // Historical performance bound only: no state reset, lease validation or
    // disposal semantics. This is intentionally not the production pool.
    [Benchmark(Description = "Historical queue prototype; no reset")]
    public int RentReturnPrototypePool()
    {
        if (!_unsafePrototypePool.TryDequeue(out var context))
            throw new InvalidOperationException("The single-threaded benchmark pool was empty.");
        try
        {
            return context.TouchSets(AccessCount);
        }
        finally
        {
            _unsafePrototypePool.Enqueue(context);
        }
    }

    private static IEnumerable<int> ReadIntValues(string variable, params int[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(value => value is 100 or 300)
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<string> ReadStringValues(string variable, params string[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .Where(value => value is "one" or "ten" or "all")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<bool> ReadBoolValues(string variable, params bool[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => bool.Parse(value))
            .Distinct()
            .ToArray();
    }
}

internal static class LargeModelBenchmarkSettings
{
    public static int ModelSize { get; set; }
    public static bool UseFluent { get; set; }
}

public class LargeModelContext : DbContext
{
    private readonly bool _cacheModels;
    private IReadOnlyList<EntityModel>? _cachedModels;

    public LargeModelContext(NpgsqlDataSource dataSource, bool cacheModels = false)
        : base(builder => builder.UseNpgsql(dataSource))
    {
        _cacheModels = cacheModels;
    }

    protected override IReadOnlyList<EntityModel> GetEntityModels()
        => _cacheModels ? _cachedModels ??= base.GetEntityModels() : base.GetEntityModels();

    public int TouchSets(int count)
    {
        var accessors = SetAccessors;
        var actual = Math.Min(count, Math.Min(LargeModelBenchmarkSettings.ModelSize, accessors.Length));
        for (var i = 0; i < actual; i++)
            accessors[i](this);
        return actual;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!LargeModelBenchmarkSettings.UseFluent)
            return;

            modelBuilder.Entity<LargeModelEntity001>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity002>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity003>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity004>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity005>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity006>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity007>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity008>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity009>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity010>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity011>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity012>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity013>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity014>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity015>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity016>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity017>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity018>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity019>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity020>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity021>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity022>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity023>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity024>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity025>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity026>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity027>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity028>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity029>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity030>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity031>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity032>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity033>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity034>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity035>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity036>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity037>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity038>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity039>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity040>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity041>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity042>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity043>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity044>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity045>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity046>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity047>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity048>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity049>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity050>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity051>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity052>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity053>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity054>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity055>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity056>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity057>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity058>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity059>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity060>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity061>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity062>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity063>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity064>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity065>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity066>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity067>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity068>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity069>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity070>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity071>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity072>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity073>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity074>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity075>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity076>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity077>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity078>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity079>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity080>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity081>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity082>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity083>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity084>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity085>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity086>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity087>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity088>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity089>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity090>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity091>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity092>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity093>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity094>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity095>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity096>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity097>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity098>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity099>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity100>(entity => entity.HasComment("model-cache-benchmark"));
            if (LargeModelBenchmarkSettings.ModelSize == 100)
                return;
            modelBuilder.Entity<LargeModelEntity101>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity102>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity103>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity104>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity105>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity106>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity107>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity108>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity109>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity110>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity111>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity112>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity113>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity114>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity115>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity116>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity117>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity118>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity119>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity120>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity121>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity122>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity123>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity124>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity125>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity126>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity127>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity128>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity129>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity130>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity131>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity132>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity133>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity134>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity135>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity136>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity137>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity138>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity139>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity140>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity141>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity142>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity143>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity144>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity145>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity146>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity147>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity148>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity149>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity150>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity151>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity152>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity153>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity154>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity155>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity156>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity157>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity158>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity159>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity160>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity161>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity162>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity163>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity164>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity165>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity166>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity167>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity168>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity169>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity170>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity171>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity172>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity173>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity174>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity175>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity176>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity177>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity178>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity179>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity180>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity181>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity182>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity183>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity184>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity185>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity186>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity187>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity188>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity189>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity190>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity191>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity192>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity193>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity194>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity195>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity196>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity197>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity198>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity199>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity200>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity201>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity202>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity203>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity204>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity205>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity206>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity207>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity208>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity209>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity210>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity211>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity212>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity213>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity214>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity215>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity216>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity217>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity218>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity219>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity220>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity221>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity222>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity223>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity224>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity225>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity226>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity227>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity228>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity229>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity230>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity231>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity232>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity233>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity234>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity235>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity236>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity237>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity238>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity239>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity240>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity241>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity242>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity243>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity244>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity245>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity246>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity247>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity248>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity249>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity250>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity251>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity252>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity253>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity254>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity255>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity256>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity257>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity258>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity259>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity260>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity261>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity262>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity263>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity264>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity265>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity266>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity267>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity268>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity269>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity270>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity271>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity272>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity273>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity274>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity275>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity276>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity277>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity278>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity279>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity280>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity281>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity282>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity283>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity284>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity285>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity286>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity287>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity288>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity289>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity290>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity291>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity292>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity293>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity294>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity295>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity296>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity297>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity298>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity299>(entity => entity.HasComment("model-cache-benchmark"));
            modelBuilder.Entity<LargeModelEntity300>(entity => entity.HasComment("model-cache-benchmark"));
    }

    private static readonly Action<LargeModelContext>[] SetAccessors =
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
        static context => _ = context.Entity010,
        static context => _ = context.Entity011,
        static context => _ = context.Entity012,
        static context => _ = context.Entity013,
        static context => _ = context.Entity014,
        static context => _ = context.Entity015,
        static context => _ = context.Entity016,
        static context => _ = context.Entity017,
        static context => _ = context.Entity018,
        static context => _ = context.Entity019,
        static context => _ = context.Entity020,
        static context => _ = context.Entity021,
        static context => _ = context.Entity022,
        static context => _ = context.Entity023,
        static context => _ = context.Entity024,
        static context => _ = context.Entity025,
        static context => _ = context.Entity026,
        static context => _ = context.Entity027,
        static context => _ = context.Entity028,
        static context => _ = context.Entity029,
        static context => _ = context.Entity030,
        static context => _ = context.Entity031,
        static context => _ = context.Entity032,
        static context => _ = context.Entity033,
        static context => _ = context.Entity034,
        static context => _ = context.Entity035,
        static context => _ = context.Entity036,
        static context => _ = context.Entity037,
        static context => _ = context.Entity038,
        static context => _ = context.Entity039,
        static context => _ = context.Entity040,
        static context => _ = context.Entity041,
        static context => _ = context.Entity042,
        static context => _ = context.Entity043,
        static context => _ = context.Entity044,
        static context => _ = context.Entity045,
        static context => _ = context.Entity046,
        static context => _ = context.Entity047,
        static context => _ = context.Entity048,
        static context => _ = context.Entity049,
        static context => _ = context.Entity050,
        static context => _ = context.Entity051,
        static context => _ = context.Entity052,
        static context => _ = context.Entity053,
        static context => _ = context.Entity054,
        static context => _ = context.Entity055,
        static context => _ = context.Entity056,
        static context => _ = context.Entity057,
        static context => _ = context.Entity058,
        static context => _ = context.Entity059,
        static context => _ = context.Entity060,
        static context => _ = context.Entity061,
        static context => _ = context.Entity062,
        static context => _ = context.Entity063,
        static context => _ = context.Entity064,
        static context => _ = context.Entity065,
        static context => _ = context.Entity066,
        static context => _ = context.Entity067,
        static context => _ = context.Entity068,
        static context => _ = context.Entity069,
        static context => _ = context.Entity070,
        static context => _ = context.Entity071,
        static context => _ = context.Entity072,
        static context => _ = context.Entity073,
        static context => _ = context.Entity074,
        static context => _ = context.Entity075,
        static context => _ = context.Entity076,
        static context => _ = context.Entity077,
        static context => _ = context.Entity078,
        static context => _ = context.Entity079,
        static context => _ = context.Entity080,
        static context => _ = context.Entity081,
        static context => _ = context.Entity082,
        static context => _ = context.Entity083,
        static context => _ = context.Entity084,
        static context => _ = context.Entity085,
        static context => _ = context.Entity086,
        static context => _ = context.Entity087,
        static context => _ = context.Entity088,
        static context => _ = context.Entity089,
        static context => _ = context.Entity090,
        static context => _ = context.Entity091,
        static context => _ = context.Entity092,
        static context => _ = context.Entity093,
        static context => _ = context.Entity094,
        static context => _ = context.Entity095,
        static context => _ = context.Entity096,
        static context => _ = context.Entity097,
        static context => _ = context.Entity098,
        static context => _ = context.Entity099,
        static context => _ = context.Entity100,
        static context => _ = context.Entity101,
        static context => _ = context.Entity102,
        static context => _ = context.Entity103,
        static context => _ = context.Entity104,
        static context => _ = context.Entity105,
        static context => _ = context.Entity106,
        static context => _ = context.Entity107,
        static context => _ = context.Entity108,
        static context => _ = context.Entity109,
        static context => _ = context.Entity110,
        static context => _ = context.Entity111,
        static context => _ = context.Entity112,
        static context => _ = context.Entity113,
        static context => _ = context.Entity114,
        static context => _ = context.Entity115,
        static context => _ = context.Entity116,
        static context => _ = context.Entity117,
        static context => _ = context.Entity118,
        static context => _ = context.Entity119,
        static context => _ = context.Entity120,
        static context => _ = context.Entity121,
        static context => _ = context.Entity122,
        static context => _ = context.Entity123,
        static context => _ = context.Entity124,
        static context => _ = context.Entity125,
        static context => _ = context.Entity126,
        static context => _ = context.Entity127,
        static context => _ = context.Entity128,
        static context => _ = context.Entity129,
        static context => _ = context.Entity130,
        static context => _ = context.Entity131,
        static context => _ = context.Entity132,
        static context => _ = context.Entity133,
        static context => _ = context.Entity134,
        static context => _ = context.Entity135,
        static context => _ = context.Entity136,
        static context => _ = context.Entity137,
        static context => _ = context.Entity138,
        static context => _ = context.Entity139,
        static context => _ = context.Entity140,
        static context => _ = context.Entity141,
        static context => _ = context.Entity142,
        static context => _ = context.Entity143,
        static context => _ = context.Entity144,
        static context => _ = context.Entity145,
        static context => _ = context.Entity146,
        static context => _ = context.Entity147,
        static context => _ = context.Entity148,
        static context => _ = context.Entity149,
        static context => _ = context.Entity150,
        static context => _ = context.Entity151,
        static context => _ = context.Entity152,
        static context => _ = context.Entity153,
        static context => _ = context.Entity154,
        static context => _ = context.Entity155,
        static context => _ = context.Entity156,
        static context => _ = context.Entity157,
        static context => _ = context.Entity158,
        static context => _ = context.Entity159,
        static context => _ = context.Entity160,
        static context => _ = context.Entity161,
        static context => _ = context.Entity162,
        static context => _ = context.Entity163,
        static context => _ = context.Entity164,
        static context => _ = context.Entity165,
        static context => _ = context.Entity166,
        static context => _ = context.Entity167,
        static context => _ = context.Entity168,
        static context => _ = context.Entity169,
        static context => _ = context.Entity170,
        static context => _ = context.Entity171,
        static context => _ = context.Entity172,
        static context => _ = context.Entity173,
        static context => _ = context.Entity174,
        static context => _ = context.Entity175,
        static context => _ = context.Entity176,
        static context => _ = context.Entity177,
        static context => _ = context.Entity178,
        static context => _ = context.Entity179,
        static context => _ = context.Entity180,
        static context => _ = context.Entity181,
        static context => _ = context.Entity182,
        static context => _ = context.Entity183,
        static context => _ = context.Entity184,
        static context => _ = context.Entity185,
        static context => _ = context.Entity186,
        static context => _ = context.Entity187,
        static context => _ = context.Entity188,
        static context => _ = context.Entity189,
        static context => _ = context.Entity190,
        static context => _ = context.Entity191,
        static context => _ = context.Entity192,
        static context => _ = context.Entity193,
        static context => _ = context.Entity194,
        static context => _ = context.Entity195,
        static context => _ = context.Entity196,
        static context => _ = context.Entity197,
        static context => _ = context.Entity198,
        static context => _ = context.Entity199,
        static context => _ = context.Entity200,
        static context => _ = context.Entity201,
        static context => _ = context.Entity202,
        static context => _ = context.Entity203,
        static context => _ = context.Entity204,
        static context => _ = context.Entity205,
        static context => _ = context.Entity206,
        static context => _ = context.Entity207,
        static context => _ = context.Entity208,
        static context => _ = context.Entity209,
        static context => _ = context.Entity210,
        static context => _ = context.Entity211,
        static context => _ = context.Entity212,
        static context => _ = context.Entity213,
        static context => _ = context.Entity214,
        static context => _ = context.Entity215,
        static context => _ = context.Entity216,
        static context => _ = context.Entity217,
        static context => _ = context.Entity218,
        static context => _ = context.Entity219,
        static context => _ = context.Entity220,
        static context => _ = context.Entity221,
        static context => _ = context.Entity222,
        static context => _ = context.Entity223,
        static context => _ = context.Entity224,
        static context => _ = context.Entity225,
        static context => _ = context.Entity226,
        static context => _ = context.Entity227,
        static context => _ = context.Entity228,
        static context => _ = context.Entity229,
        static context => _ = context.Entity230,
        static context => _ = context.Entity231,
        static context => _ = context.Entity232,
        static context => _ = context.Entity233,
        static context => _ = context.Entity234,
        static context => _ = context.Entity235,
        static context => _ = context.Entity236,
        static context => _ = context.Entity237,
        static context => _ = context.Entity238,
        static context => _ = context.Entity239,
        static context => _ = context.Entity240,
        static context => _ = context.Entity241,
        static context => _ = context.Entity242,
        static context => _ = context.Entity243,
        static context => _ = context.Entity244,
        static context => _ = context.Entity245,
        static context => _ = context.Entity246,
        static context => _ = context.Entity247,
        static context => _ = context.Entity248,
        static context => _ = context.Entity249,
        static context => _ = context.Entity250,
        static context => _ = context.Entity251,
        static context => _ = context.Entity252,
        static context => _ = context.Entity253,
        static context => _ = context.Entity254,
        static context => _ = context.Entity255,
        static context => _ = context.Entity256,
        static context => _ = context.Entity257,
        static context => _ = context.Entity258,
        static context => _ = context.Entity259,
        static context => _ = context.Entity260,
        static context => _ = context.Entity261,
        static context => _ = context.Entity262,
        static context => _ = context.Entity263,
        static context => _ = context.Entity264,
        static context => _ = context.Entity265,
        static context => _ = context.Entity266,
        static context => _ = context.Entity267,
        static context => _ = context.Entity268,
        static context => _ = context.Entity269,
        static context => _ = context.Entity270,
        static context => _ = context.Entity271,
        static context => _ = context.Entity272,
        static context => _ = context.Entity273,
        static context => _ = context.Entity274,
        static context => _ = context.Entity275,
        static context => _ = context.Entity276,
        static context => _ = context.Entity277,
        static context => _ = context.Entity278,
        static context => _ = context.Entity279,
        static context => _ = context.Entity280,
        static context => _ = context.Entity281,
        static context => _ = context.Entity282,
        static context => _ = context.Entity283,
        static context => _ = context.Entity284,
        static context => _ = context.Entity285,
        static context => _ = context.Entity286,
        static context => _ = context.Entity287,
        static context => _ = context.Entity288,
        static context => _ = context.Entity289,
        static context => _ = context.Entity290,
        static context => _ = context.Entity291,
        static context => _ = context.Entity292,
        static context => _ = context.Entity293,
        static context => _ = context.Entity294,
        static context => _ = context.Entity295,
        static context => _ = context.Entity296,
        static context => _ = context.Entity297,
        static context => _ = context.Entity298,
        static context => _ = context.Entity299,
        static context => _ = context.Entity300,
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
    public DbSet<LargeModelEntity011> Entity011 => Set<LargeModelEntity011>();
    public DbSet<LargeModelEntity012> Entity012 => Set<LargeModelEntity012>();
    public DbSet<LargeModelEntity013> Entity013 => Set<LargeModelEntity013>();
    public DbSet<LargeModelEntity014> Entity014 => Set<LargeModelEntity014>();
    public DbSet<LargeModelEntity015> Entity015 => Set<LargeModelEntity015>();
    public DbSet<LargeModelEntity016> Entity016 => Set<LargeModelEntity016>();
    public DbSet<LargeModelEntity017> Entity017 => Set<LargeModelEntity017>();
    public DbSet<LargeModelEntity018> Entity018 => Set<LargeModelEntity018>();
    public DbSet<LargeModelEntity019> Entity019 => Set<LargeModelEntity019>();
    public DbSet<LargeModelEntity020> Entity020 => Set<LargeModelEntity020>();
    public DbSet<LargeModelEntity021> Entity021 => Set<LargeModelEntity021>();
    public DbSet<LargeModelEntity022> Entity022 => Set<LargeModelEntity022>();
    public DbSet<LargeModelEntity023> Entity023 => Set<LargeModelEntity023>();
    public DbSet<LargeModelEntity024> Entity024 => Set<LargeModelEntity024>();
    public DbSet<LargeModelEntity025> Entity025 => Set<LargeModelEntity025>();
    public DbSet<LargeModelEntity026> Entity026 => Set<LargeModelEntity026>();
    public DbSet<LargeModelEntity027> Entity027 => Set<LargeModelEntity027>();
    public DbSet<LargeModelEntity028> Entity028 => Set<LargeModelEntity028>();
    public DbSet<LargeModelEntity029> Entity029 => Set<LargeModelEntity029>();
    public DbSet<LargeModelEntity030> Entity030 => Set<LargeModelEntity030>();
    public DbSet<LargeModelEntity031> Entity031 => Set<LargeModelEntity031>();
    public DbSet<LargeModelEntity032> Entity032 => Set<LargeModelEntity032>();
    public DbSet<LargeModelEntity033> Entity033 => Set<LargeModelEntity033>();
    public DbSet<LargeModelEntity034> Entity034 => Set<LargeModelEntity034>();
    public DbSet<LargeModelEntity035> Entity035 => Set<LargeModelEntity035>();
    public DbSet<LargeModelEntity036> Entity036 => Set<LargeModelEntity036>();
    public DbSet<LargeModelEntity037> Entity037 => Set<LargeModelEntity037>();
    public DbSet<LargeModelEntity038> Entity038 => Set<LargeModelEntity038>();
    public DbSet<LargeModelEntity039> Entity039 => Set<LargeModelEntity039>();
    public DbSet<LargeModelEntity040> Entity040 => Set<LargeModelEntity040>();
    public DbSet<LargeModelEntity041> Entity041 => Set<LargeModelEntity041>();
    public DbSet<LargeModelEntity042> Entity042 => Set<LargeModelEntity042>();
    public DbSet<LargeModelEntity043> Entity043 => Set<LargeModelEntity043>();
    public DbSet<LargeModelEntity044> Entity044 => Set<LargeModelEntity044>();
    public DbSet<LargeModelEntity045> Entity045 => Set<LargeModelEntity045>();
    public DbSet<LargeModelEntity046> Entity046 => Set<LargeModelEntity046>();
    public DbSet<LargeModelEntity047> Entity047 => Set<LargeModelEntity047>();
    public DbSet<LargeModelEntity048> Entity048 => Set<LargeModelEntity048>();
    public DbSet<LargeModelEntity049> Entity049 => Set<LargeModelEntity049>();
    public DbSet<LargeModelEntity050> Entity050 => Set<LargeModelEntity050>();
    public DbSet<LargeModelEntity051> Entity051 => Set<LargeModelEntity051>();
    public DbSet<LargeModelEntity052> Entity052 => Set<LargeModelEntity052>();
    public DbSet<LargeModelEntity053> Entity053 => Set<LargeModelEntity053>();
    public DbSet<LargeModelEntity054> Entity054 => Set<LargeModelEntity054>();
    public DbSet<LargeModelEntity055> Entity055 => Set<LargeModelEntity055>();
    public DbSet<LargeModelEntity056> Entity056 => Set<LargeModelEntity056>();
    public DbSet<LargeModelEntity057> Entity057 => Set<LargeModelEntity057>();
    public DbSet<LargeModelEntity058> Entity058 => Set<LargeModelEntity058>();
    public DbSet<LargeModelEntity059> Entity059 => Set<LargeModelEntity059>();
    public DbSet<LargeModelEntity060> Entity060 => Set<LargeModelEntity060>();
    public DbSet<LargeModelEntity061> Entity061 => Set<LargeModelEntity061>();
    public DbSet<LargeModelEntity062> Entity062 => Set<LargeModelEntity062>();
    public DbSet<LargeModelEntity063> Entity063 => Set<LargeModelEntity063>();
    public DbSet<LargeModelEntity064> Entity064 => Set<LargeModelEntity064>();
    public DbSet<LargeModelEntity065> Entity065 => Set<LargeModelEntity065>();
    public DbSet<LargeModelEntity066> Entity066 => Set<LargeModelEntity066>();
    public DbSet<LargeModelEntity067> Entity067 => Set<LargeModelEntity067>();
    public DbSet<LargeModelEntity068> Entity068 => Set<LargeModelEntity068>();
    public DbSet<LargeModelEntity069> Entity069 => Set<LargeModelEntity069>();
    public DbSet<LargeModelEntity070> Entity070 => Set<LargeModelEntity070>();
    public DbSet<LargeModelEntity071> Entity071 => Set<LargeModelEntity071>();
    public DbSet<LargeModelEntity072> Entity072 => Set<LargeModelEntity072>();
    public DbSet<LargeModelEntity073> Entity073 => Set<LargeModelEntity073>();
    public DbSet<LargeModelEntity074> Entity074 => Set<LargeModelEntity074>();
    public DbSet<LargeModelEntity075> Entity075 => Set<LargeModelEntity075>();
    public DbSet<LargeModelEntity076> Entity076 => Set<LargeModelEntity076>();
    public DbSet<LargeModelEntity077> Entity077 => Set<LargeModelEntity077>();
    public DbSet<LargeModelEntity078> Entity078 => Set<LargeModelEntity078>();
    public DbSet<LargeModelEntity079> Entity079 => Set<LargeModelEntity079>();
    public DbSet<LargeModelEntity080> Entity080 => Set<LargeModelEntity080>();
    public DbSet<LargeModelEntity081> Entity081 => Set<LargeModelEntity081>();
    public DbSet<LargeModelEntity082> Entity082 => Set<LargeModelEntity082>();
    public DbSet<LargeModelEntity083> Entity083 => Set<LargeModelEntity083>();
    public DbSet<LargeModelEntity084> Entity084 => Set<LargeModelEntity084>();
    public DbSet<LargeModelEntity085> Entity085 => Set<LargeModelEntity085>();
    public DbSet<LargeModelEntity086> Entity086 => Set<LargeModelEntity086>();
    public DbSet<LargeModelEntity087> Entity087 => Set<LargeModelEntity087>();
    public DbSet<LargeModelEntity088> Entity088 => Set<LargeModelEntity088>();
    public DbSet<LargeModelEntity089> Entity089 => Set<LargeModelEntity089>();
    public DbSet<LargeModelEntity090> Entity090 => Set<LargeModelEntity090>();
    public DbSet<LargeModelEntity091> Entity091 => Set<LargeModelEntity091>();
    public DbSet<LargeModelEntity092> Entity092 => Set<LargeModelEntity092>();
    public DbSet<LargeModelEntity093> Entity093 => Set<LargeModelEntity093>();
    public DbSet<LargeModelEntity094> Entity094 => Set<LargeModelEntity094>();
    public DbSet<LargeModelEntity095> Entity095 => Set<LargeModelEntity095>();
    public DbSet<LargeModelEntity096> Entity096 => Set<LargeModelEntity096>();
    public DbSet<LargeModelEntity097> Entity097 => Set<LargeModelEntity097>();
    public DbSet<LargeModelEntity098> Entity098 => Set<LargeModelEntity098>();
    public DbSet<LargeModelEntity099> Entity099 => Set<LargeModelEntity099>();
    public DbSet<LargeModelEntity100> Entity100 => Set<LargeModelEntity100>();
    public DbSet<LargeModelEntity101> Entity101 => Set<LargeModelEntity101>();
    public DbSet<LargeModelEntity102> Entity102 => Set<LargeModelEntity102>();
    public DbSet<LargeModelEntity103> Entity103 => Set<LargeModelEntity103>();
    public DbSet<LargeModelEntity104> Entity104 => Set<LargeModelEntity104>();
    public DbSet<LargeModelEntity105> Entity105 => Set<LargeModelEntity105>();
    public DbSet<LargeModelEntity106> Entity106 => Set<LargeModelEntity106>();
    public DbSet<LargeModelEntity107> Entity107 => Set<LargeModelEntity107>();
    public DbSet<LargeModelEntity108> Entity108 => Set<LargeModelEntity108>();
    public DbSet<LargeModelEntity109> Entity109 => Set<LargeModelEntity109>();
    public DbSet<LargeModelEntity110> Entity110 => Set<LargeModelEntity110>();
    public DbSet<LargeModelEntity111> Entity111 => Set<LargeModelEntity111>();
    public DbSet<LargeModelEntity112> Entity112 => Set<LargeModelEntity112>();
    public DbSet<LargeModelEntity113> Entity113 => Set<LargeModelEntity113>();
    public DbSet<LargeModelEntity114> Entity114 => Set<LargeModelEntity114>();
    public DbSet<LargeModelEntity115> Entity115 => Set<LargeModelEntity115>();
    public DbSet<LargeModelEntity116> Entity116 => Set<LargeModelEntity116>();
    public DbSet<LargeModelEntity117> Entity117 => Set<LargeModelEntity117>();
    public DbSet<LargeModelEntity118> Entity118 => Set<LargeModelEntity118>();
    public DbSet<LargeModelEntity119> Entity119 => Set<LargeModelEntity119>();
    public DbSet<LargeModelEntity120> Entity120 => Set<LargeModelEntity120>();
    public DbSet<LargeModelEntity121> Entity121 => Set<LargeModelEntity121>();
    public DbSet<LargeModelEntity122> Entity122 => Set<LargeModelEntity122>();
    public DbSet<LargeModelEntity123> Entity123 => Set<LargeModelEntity123>();
    public DbSet<LargeModelEntity124> Entity124 => Set<LargeModelEntity124>();
    public DbSet<LargeModelEntity125> Entity125 => Set<LargeModelEntity125>();
    public DbSet<LargeModelEntity126> Entity126 => Set<LargeModelEntity126>();
    public DbSet<LargeModelEntity127> Entity127 => Set<LargeModelEntity127>();
    public DbSet<LargeModelEntity128> Entity128 => Set<LargeModelEntity128>();
    public DbSet<LargeModelEntity129> Entity129 => Set<LargeModelEntity129>();
    public DbSet<LargeModelEntity130> Entity130 => Set<LargeModelEntity130>();
    public DbSet<LargeModelEntity131> Entity131 => Set<LargeModelEntity131>();
    public DbSet<LargeModelEntity132> Entity132 => Set<LargeModelEntity132>();
    public DbSet<LargeModelEntity133> Entity133 => Set<LargeModelEntity133>();
    public DbSet<LargeModelEntity134> Entity134 => Set<LargeModelEntity134>();
    public DbSet<LargeModelEntity135> Entity135 => Set<LargeModelEntity135>();
    public DbSet<LargeModelEntity136> Entity136 => Set<LargeModelEntity136>();
    public DbSet<LargeModelEntity137> Entity137 => Set<LargeModelEntity137>();
    public DbSet<LargeModelEntity138> Entity138 => Set<LargeModelEntity138>();
    public DbSet<LargeModelEntity139> Entity139 => Set<LargeModelEntity139>();
    public DbSet<LargeModelEntity140> Entity140 => Set<LargeModelEntity140>();
    public DbSet<LargeModelEntity141> Entity141 => Set<LargeModelEntity141>();
    public DbSet<LargeModelEntity142> Entity142 => Set<LargeModelEntity142>();
    public DbSet<LargeModelEntity143> Entity143 => Set<LargeModelEntity143>();
    public DbSet<LargeModelEntity144> Entity144 => Set<LargeModelEntity144>();
    public DbSet<LargeModelEntity145> Entity145 => Set<LargeModelEntity145>();
    public DbSet<LargeModelEntity146> Entity146 => Set<LargeModelEntity146>();
    public DbSet<LargeModelEntity147> Entity147 => Set<LargeModelEntity147>();
    public DbSet<LargeModelEntity148> Entity148 => Set<LargeModelEntity148>();
    public DbSet<LargeModelEntity149> Entity149 => Set<LargeModelEntity149>();
    public DbSet<LargeModelEntity150> Entity150 => Set<LargeModelEntity150>();
    public DbSet<LargeModelEntity151> Entity151 => Set<LargeModelEntity151>();
    public DbSet<LargeModelEntity152> Entity152 => Set<LargeModelEntity152>();
    public DbSet<LargeModelEntity153> Entity153 => Set<LargeModelEntity153>();
    public DbSet<LargeModelEntity154> Entity154 => Set<LargeModelEntity154>();
    public DbSet<LargeModelEntity155> Entity155 => Set<LargeModelEntity155>();
    public DbSet<LargeModelEntity156> Entity156 => Set<LargeModelEntity156>();
    public DbSet<LargeModelEntity157> Entity157 => Set<LargeModelEntity157>();
    public DbSet<LargeModelEntity158> Entity158 => Set<LargeModelEntity158>();
    public DbSet<LargeModelEntity159> Entity159 => Set<LargeModelEntity159>();
    public DbSet<LargeModelEntity160> Entity160 => Set<LargeModelEntity160>();
    public DbSet<LargeModelEntity161> Entity161 => Set<LargeModelEntity161>();
    public DbSet<LargeModelEntity162> Entity162 => Set<LargeModelEntity162>();
    public DbSet<LargeModelEntity163> Entity163 => Set<LargeModelEntity163>();
    public DbSet<LargeModelEntity164> Entity164 => Set<LargeModelEntity164>();
    public DbSet<LargeModelEntity165> Entity165 => Set<LargeModelEntity165>();
    public DbSet<LargeModelEntity166> Entity166 => Set<LargeModelEntity166>();
    public DbSet<LargeModelEntity167> Entity167 => Set<LargeModelEntity167>();
    public DbSet<LargeModelEntity168> Entity168 => Set<LargeModelEntity168>();
    public DbSet<LargeModelEntity169> Entity169 => Set<LargeModelEntity169>();
    public DbSet<LargeModelEntity170> Entity170 => Set<LargeModelEntity170>();
    public DbSet<LargeModelEntity171> Entity171 => Set<LargeModelEntity171>();
    public DbSet<LargeModelEntity172> Entity172 => Set<LargeModelEntity172>();
    public DbSet<LargeModelEntity173> Entity173 => Set<LargeModelEntity173>();
    public DbSet<LargeModelEntity174> Entity174 => Set<LargeModelEntity174>();
    public DbSet<LargeModelEntity175> Entity175 => Set<LargeModelEntity175>();
    public DbSet<LargeModelEntity176> Entity176 => Set<LargeModelEntity176>();
    public DbSet<LargeModelEntity177> Entity177 => Set<LargeModelEntity177>();
    public DbSet<LargeModelEntity178> Entity178 => Set<LargeModelEntity178>();
    public DbSet<LargeModelEntity179> Entity179 => Set<LargeModelEntity179>();
    public DbSet<LargeModelEntity180> Entity180 => Set<LargeModelEntity180>();
    public DbSet<LargeModelEntity181> Entity181 => Set<LargeModelEntity181>();
    public DbSet<LargeModelEntity182> Entity182 => Set<LargeModelEntity182>();
    public DbSet<LargeModelEntity183> Entity183 => Set<LargeModelEntity183>();
    public DbSet<LargeModelEntity184> Entity184 => Set<LargeModelEntity184>();
    public DbSet<LargeModelEntity185> Entity185 => Set<LargeModelEntity185>();
    public DbSet<LargeModelEntity186> Entity186 => Set<LargeModelEntity186>();
    public DbSet<LargeModelEntity187> Entity187 => Set<LargeModelEntity187>();
    public DbSet<LargeModelEntity188> Entity188 => Set<LargeModelEntity188>();
    public DbSet<LargeModelEntity189> Entity189 => Set<LargeModelEntity189>();
    public DbSet<LargeModelEntity190> Entity190 => Set<LargeModelEntity190>();
    public DbSet<LargeModelEntity191> Entity191 => Set<LargeModelEntity191>();
    public DbSet<LargeModelEntity192> Entity192 => Set<LargeModelEntity192>();
    public DbSet<LargeModelEntity193> Entity193 => Set<LargeModelEntity193>();
    public DbSet<LargeModelEntity194> Entity194 => Set<LargeModelEntity194>();
    public DbSet<LargeModelEntity195> Entity195 => Set<LargeModelEntity195>();
    public DbSet<LargeModelEntity196> Entity196 => Set<LargeModelEntity196>();
    public DbSet<LargeModelEntity197> Entity197 => Set<LargeModelEntity197>();
    public DbSet<LargeModelEntity198> Entity198 => Set<LargeModelEntity198>();
    public DbSet<LargeModelEntity199> Entity199 => Set<LargeModelEntity199>();
    public DbSet<LargeModelEntity200> Entity200 => Set<LargeModelEntity200>();
    public DbSet<LargeModelEntity201> Entity201 => Set<LargeModelEntity201>();
    public DbSet<LargeModelEntity202> Entity202 => Set<LargeModelEntity202>();
    public DbSet<LargeModelEntity203> Entity203 => Set<LargeModelEntity203>();
    public DbSet<LargeModelEntity204> Entity204 => Set<LargeModelEntity204>();
    public DbSet<LargeModelEntity205> Entity205 => Set<LargeModelEntity205>();
    public DbSet<LargeModelEntity206> Entity206 => Set<LargeModelEntity206>();
    public DbSet<LargeModelEntity207> Entity207 => Set<LargeModelEntity207>();
    public DbSet<LargeModelEntity208> Entity208 => Set<LargeModelEntity208>();
    public DbSet<LargeModelEntity209> Entity209 => Set<LargeModelEntity209>();
    public DbSet<LargeModelEntity210> Entity210 => Set<LargeModelEntity210>();
    public DbSet<LargeModelEntity211> Entity211 => Set<LargeModelEntity211>();
    public DbSet<LargeModelEntity212> Entity212 => Set<LargeModelEntity212>();
    public DbSet<LargeModelEntity213> Entity213 => Set<LargeModelEntity213>();
    public DbSet<LargeModelEntity214> Entity214 => Set<LargeModelEntity214>();
    public DbSet<LargeModelEntity215> Entity215 => Set<LargeModelEntity215>();
    public DbSet<LargeModelEntity216> Entity216 => Set<LargeModelEntity216>();
    public DbSet<LargeModelEntity217> Entity217 => Set<LargeModelEntity217>();
    public DbSet<LargeModelEntity218> Entity218 => Set<LargeModelEntity218>();
    public DbSet<LargeModelEntity219> Entity219 => Set<LargeModelEntity219>();
    public DbSet<LargeModelEntity220> Entity220 => Set<LargeModelEntity220>();
    public DbSet<LargeModelEntity221> Entity221 => Set<LargeModelEntity221>();
    public DbSet<LargeModelEntity222> Entity222 => Set<LargeModelEntity222>();
    public DbSet<LargeModelEntity223> Entity223 => Set<LargeModelEntity223>();
    public DbSet<LargeModelEntity224> Entity224 => Set<LargeModelEntity224>();
    public DbSet<LargeModelEntity225> Entity225 => Set<LargeModelEntity225>();
    public DbSet<LargeModelEntity226> Entity226 => Set<LargeModelEntity226>();
    public DbSet<LargeModelEntity227> Entity227 => Set<LargeModelEntity227>();
    public DbSet<LargeModelEntity228> Entity228 => Set<LargeModelEntity228>();
    public DbSet<LargeModelEntity229> Entity229 => Set<LargeModelEntity229>();
    public DbSet<LargeModelEntity230> Entity230 => Set<LargeModelEntity230>();
    public DbSet<LargeModelEntity231> Entity231 => Set<LargeModelEntity231>();
    public DbSet<LargeModelEntity232> Entity232 => Set<LargeModelEntity232>();
    public DbSet<LargeModelEntity233> Entity233 => Set<LargeModelEntity233>();
    public DbSet<LargeModelEntity234> Entity234 => Set<LargeModelEntity234>();
    public DbSet<LargeModelEntity235> Entity235 => Set<LargeModelEntity235>();
    public DbSet<LargeModelEntity236> Entity236 => Set<LargeModelEntity236>();
    public DbSet<LargeModelEntity237> Entity237 => Set<LargeModelEntity237>();
    public DbSet<LargeModelEntity238> Entity238 => Set<LargeModelEntity238>();
    public DbSet<LargeModelEntity239> Entity239 => Set<LargeModelEntity239>();
    public DbSet<LargeModelEntity240> Entity240 => Set<LargeModelEntity240>();
    public DbSet<LargeModelEntity241> Entity241 => Set<LargeModelEntity241>();
    public DbSet<LargeModelEntity242> Entity242 => Set<LargeModelEntity242>();
    public DbSet<LargeModelEntity243> Entity243 => Set<LargeModelEntity243>();
    public DbSet<LargeModelEntity244> Entity244 => Set<LargeModelEntity244>();
    public DbSet<LargeModelEntity245> Entity245 => Set<LargeModelEntity245>();
    public DbSet<LargeModelEntity246> Entity246 => Set<LargeModelEntity246>();
    public DbSet<LargeModelEntity247> Entity247 => Set<LargeModelEntity247>();
    public DbSet<LargeModelEntity248> Entity248 => Set<LargeModelEntity248>();
    public DbSet<LargeModelEntity249> Entity249 => Set<LargeModelEntity249>();
    public DbSet<LargeModelEntity250> Entity250 => Set<LargeModelEntity250>();
    public DbSet<LargeModelEntity251> Entity251 => Set<LargeModelEntity251>();
    public DbSet<LargeModelEntity252> Entity252 => Set<LargeModelEntity252>();
    public DbSet<LargeModelEntity253> Entity253 => Set<LargeModelEntity253>();
    public DbSet<LargeModelEntity254> Entity254 => Set<LargeModelEntity254>();
    public DbSet<LargeModelEntity255> Entity255 => Set<LargeModelEntity255>();
    public DbSet<LargeModelEntity256> Entity256 => Set<LargeModelEntity256>();
    public DbSet<LargeModelEntity257> Entity257 => Set<LargeModelEntity257>();
    public DbSet<LargeModelEntity258> Entity258 => Set<LargeModelEntity258>();
    public DbSet<LargeModelEntity259> Entity259 => Set<LargeModelEntity259>();
    public DbSet<LargeModelEntity260> Entity260 => Set<LargeModelEntity260>();
    public DbSet<LargeModelEntity261> Entity261 => Set<LargeModelEntity261>();
    public DbSet<LargeModelEntity262> Entity262 => Set<LargeModelEntity262>();
    public DbSet<LargeModelEntity263> Entity263 => Set<LargeModelEntity263>();
    public DbSet<LargeModelEntity264> Entity264 => Set<LargeModelEntity264>();
    public DbSet<LargeModelEntity265> Entity265 => Set<LargeModelEntity265>();
    public DbSet<LargeModelEntity266> Entity266 => Set<LargeModelEntity266>();
    public DbSet<LargeModelEntity267> Entity267 => Set<LargeModelEntity267>();
    public DbSet<LargeModelEntity268> Entity268 => Set<LargeModelEntity268>();
    public DbSet<LargeModelEntity269> Entity269 => Set<LargeModelEntity269>();
    public DbSet<LargeModelEntity270> Entity270 => Set<LargeModelEntity270>();
    public DbSet<LargeModelEntity271> Entity271 => Set<LargeModelEntity271>();
    public DbSet<LargeModelEntity272> Entity272 => Set<LargeModelEntity272>();
    public DbSet<LargeModelEntity273> Entity273 => Set<LargeModelEntity273>();
    public DbSet<LargeModelEntity274> Entity274 => Set<LargeModelEntity274>();
    public DbSet<LargeModelEntity275> Entity275 => Set<LargeModelEntity275>();
    public DbSet<LargeModelEntity276> Entity276 => Set<LargeModelEntity276>();
    public DbSet<LargeModelEntity277> Entity277 => Set<LargeModelEntity277>();
    public DbSet<LargeModelEntity278> Entity278 => Set<LargeModelEntity278>();
    public DbSet<LargeModelEntity279> Entity279 => Set<LargeModelEntity279>();
    public DbSet<LargeModelEntity280> Entity280 => Set<LargeModelEntity280>();
    public DbSet<LargeModelEntity281> Entity281 => Set<LargeModelEntity281>();
    public DbSet<LargeModelEntity282> Entity282 => Set<LargeModelEntity282>();
    public DbSet<LargeModelEntity283> Entity283 => Set<LargeModelEntity283>();
    public DbSet<LargeModelEntity284> Entity284 => Set<LargeModelEntity284>();
    public DbSet<LargeModelEntity285> Entity285 => Set<LargeModelEntity285>();
    public DbSet<LargeModelEntity286> Entity286 => Set<LargeModelEntity286>();
    public DbSet<LargeModelEntity287> Entity287 => Set<LargeModelEntity287>();
    public DbSet<LargeModelEntity288> Entity288 => Set<LargeModelEntity288>();
    public DbSet<LargeModelEntity289> Entity289 => Set<LargeModelEntity289>();
    public DbSet<LargeModelEntity290> Entity290 => Set<LargeModelEntity290>();
    public DbSet<LargeModelEntity291> Entity291 => Set<LargeModelEntity291>();
    public DbSet<LargeModelEntity292> Entity292 => Set<LargeModelEntity292>();
    public DbSet<LargeModelEntity293> Entity293 => Set<LargeModelEntity293>();
    public DbSet<LargeModelEntity294> Entity294 => Set<LargeModelEntity294>();
    public DbSet<LargeModelEntity295> Entity295 => Set<LargeModelEntity295>();
    public DbSet<LargeModelEntity296> Entity296 => Set<LargeModelEntity296>();
    public DbSet<LargeModelEntity297> Entity297 => Set<LargeModelEntity297>();
    public DbSet<LargeModelEntity298> Entity298 => Set<LargeModelEntity298>();
    public DbSet<LargeModelEntity299> Entity299 => Set<LargeModelEntity299>();
    public DbSet<LargeModelEntity300> Entity300 => Set<LargeModelEntity300>();
}

public sealed class LargeModelEntity001
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity002
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity003
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity004
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity005
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity006
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity007
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity008
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity009
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity010
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity011
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity012
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity013
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity014
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity015
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity016
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity017
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity018
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity019
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity020
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity021
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity022
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity023
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity024
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity025
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity026
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity027
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity028
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity029
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity030
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity031
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity032
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity033
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity034
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity035
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity036
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity037
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity038
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity039
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity040
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity041
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity042
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity043
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity044
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity045
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity046
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity047
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity048
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity049
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity050
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity051
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity052
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity053
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity054
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity055
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity056
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity057
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity058
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity059
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity060
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity061
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity062
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity063
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity064
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity065
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity066
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity067
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity068
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity069
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity070
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity071
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity072
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity073
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity074
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity075
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity076
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity077
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity078
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity079
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity080
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity081
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity082
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity083
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity084
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity085
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity086
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity087
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity088
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity089
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity090
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity091
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity092
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity093
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity094
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity095
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity096
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity097
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity098
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity099
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity100
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity101
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity102
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity103
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity104
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity105
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity106
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity107
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity108
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity109
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity110
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity111
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity112
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity113
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity114
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity115
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity116
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity117
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity118
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity119
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity120
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity121
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity122
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity123
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity124
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity125
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity126
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity127
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity128
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity129
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity130
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity131
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity132
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity133
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity134
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity135
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity136
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity137
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity138
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity139
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity140
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity141
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity142
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity143
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity144
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity145
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity146
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity147
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity148
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity149
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity150
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity151
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity152
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity153
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity154
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity155
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity156
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity157
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity158
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity159
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity160
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity161
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity162
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity163
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity164
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity165
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity166
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity167
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity168
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity169
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity170
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity171
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity172
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity173
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity174
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity175
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity176
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity177
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity178
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity179
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity180
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity181
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity182
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity183
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity184
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity185
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity186
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity187
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity188
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity189
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity190
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity191
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity192
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity193
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity194
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity195
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity196
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity197
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity198
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity199
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity200
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity201
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity202
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity203
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity204
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity205
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity206
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity207
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity208
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity209
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity210
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity211
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity212
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity213
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity214
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity215
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity216
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity217
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity218
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity219
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity220
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity221
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity222
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity223
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity224
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity225
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity226
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity227
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity228
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity229
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity230
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity231
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity232
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity233
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity234
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity235
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity236
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity237
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity238
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity239
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity240
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity241
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity242
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity243
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity244
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity245
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity246
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity247
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity248
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity249
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity250
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity251
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity252
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity253
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity254
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity255
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity256
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity257
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity258
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity259
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity260
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity261
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity262
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity263
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity264
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity265
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity266
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity267
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity268
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity269
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity270
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity271
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity272
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity273
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity274
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity275
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity276
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity277
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity278
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity279
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity280
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity281
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity282
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity283
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity284
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity285
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity286
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity287
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity288
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity289
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity290
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity291
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity292
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity293
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity294
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity295
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity296
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity297
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity298
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity299
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LargeModelEntity300
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
