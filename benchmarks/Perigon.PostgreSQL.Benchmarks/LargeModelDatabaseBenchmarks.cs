using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Benchmarks;

/// <summary>
/// Measures the same large-model context paths as <see cref="LargeModelBenchmarks"/>,
/// but executes the count queries against a disposable PostgreSQL schema.
/// The checked-in report is historical: it was captured before the production
/// DbContext gained its instance-level model cache. The ordinary path now caches
/// resolved models, while the old cache flag remains only as a historical control.
/// The queue prototype deliberately has no reset or lease semantics and is not a
/// production context-pooling implementation; use DbContextPoolBenchmarks for
/// the real AddDbContextPool comparison.
/// </summary>
[MemoryDiagnoser]
public class LargeModelDatabaseBenchmarks
{
    private static IReadOnlyList<EntityModel>? _allGeneratedModels;
    private NpgsqlDataSource _dataSource = null!;
    private LargeModelContext _warmContext = null!;
    private bool _schemaCreated;
    private readonly ConcurrentQueue<LargeModelContext> _prototypePool = new();

    [ParamsSource(nameof(ModelSizes))]
    public int ModelSize { get; set; }

    [ParamsSource(nameof(AccessProfiles))]
    public string AccessProfile { get; set; } = "one";

    [ParamsSource(nameof(FluentValues))]
    public bool Fluent { get; set; }

    public IEnumerable<int> ModelSizes => ReadIntValues("PERIGON_LIVE_MODEL_SIZES", 100, 300);

    public IEnumerable<string> AccessProfiles => ReadStringValues("PERIGON_LIVE_MODEL_ACCESSES", "one", "ten");

    public IEnumerable<bool> FluentValues => ReadBoolValues("PERIGON_LIVE_MODEL_FLUENT", false, true);

    private int AccessCount => AccessProfile switch
    {
        "one" => 1,
        "ten" => Math.Min(10, ModelSize),
        _ => throw new ArgumentOutOfRangeException(nameof(AccessProfile), AccessProfile)
    };

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        LargeModelBenchmarkSettings.ModelSize = ModelSize;
        LargeModelBenchmarkSettings.UseFluent = Fluent;

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
        var connectionString = Environment.GetEnvironmentVariable("PERIGON_BENCHMARK_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set PERIGON_BENCHMARK_POSTGRES_CONNECTION_STRING to a disposable PostgreSQL database before running the live benchmark.");
        }

        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 1,
            MaxPoolSize = 64,
            SearchPath = BenchmarkConfiguration.SchemaName
        };
        _dataSource = NpgsqlDataSource.Create(connectionBuilder.ConnectionString);

        try
        {
            var models = generated.Take(AccessCount).ToArray();
            await CreateSchemaAndTablesAsync(models).ConfigureAwait(false);

            using (var current = new LargeModelContext(_dataSource))
            {
                var result = await ExecuteQueriesAsync(current, AccessCount).ConfigureAwait(false);
                EnsureResult(result, AccessCount, "current model warmup");
            }

            using (var cached = new LargeModelContext(_dataSource, cacheModels: true))
            {
                var result = await ExecuteQueriesAsync(cached, AccessCount).ConfigureAwait(false);
                EnsureResult(result, AccessCount, "model cache warmup");
            }

            _warmContext = new LargeModelContext(_dataSource, cacheModels: true);
            var warmResult = await ExecuteQueriesAsync(_warmContext, AccessCount).ConfigureAwait(false);
            EnsureResult(warmResult, AccessCount, "prototype pool warmup");
            _prototypePool.Enqueue(_warmContext);
        }
        catch
        {
            try
            {
                if (_schemaCreated)
                    await BenchmarkConfiguration.DropSchemaAsync(_dataSource).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the setup failure; cleanup is best effort.
            }

            await _dataSource.DisposeAsync().ConfigureAwait(false);
            _dataSource = null!;
            throw;
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        _warmContext?.Dispose();
        if (_dataSource is not null)
        {
            if (_schemaCreated)
                await BenchmarkConfiguration.DropSchemaAsync(_dataSource).ConfigureAwait(false);
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Benchmark(Baseline = true, Description = "PostgreSQL; new context; current model path")]
    public async Task<int> NewContextCurrentModelPath()
    {
        using var context = new LargeModelContext(_dataSource);
        return await ExecuteQueriesAsync(context, AccessCount).ConfigureAwait(false);
    }

    [Benchmark(Description = "PostgreSQL; new context; legacy cache flag (historical control)")]
    public async Task<int> NewContextCachedModelPath()
    {
        using var context = new LargeModelContext(_dataSource, cacheModels: true);
        return await ExecuteQueriesAsync(context, AccessCount).ConfigureAwait(false);
    }

    [Benchmark(Description = "PostgreSQL; historical queue prototype; no reset")]
    public async Task<int> RentReturnPrototypePool()
    {
        if (!_prototypePool.TryDequeue(out var context))
            throw new InvalidOperationException("The single-threaded benchmark pool was empty.");

        try
        {
            return await ExecuteQueriesAsync(context, AccessCount).ConfigureAwait(false);
        }
        finally
        {
            _prototypePool.Enqueue(context);
        }
    }

    private async Task CreateSchemaAndTablesAsync(IReadOnlyList<EntityModel> models)
    {
        await using var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = $"CREATE SCHEMA {QuoteIdentifier(BenchmarkConfiguration.SchemaName)}";
            await schemaCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            _schemaCreated = true;
        }

        foreach (var model in models)
        {
            var id = model.GetColumn(nameof(LargeModelEntity001.Id));
            var name = model.GetColumn(nameof(LargeModelEntity001.Name));
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE {QuoteIdentifier(model.TableName)} (
                    {QuoteIdentifier(id.ColumnName)} integer PRIMARY KEY,
                    {QuoteIdentifier(name.ColumnName)} text NOT NULL
                );
                INSERT INTO {QuoteIdentifier(model.TableName)} ({QuoteIdentifier(id.ColumnName)}, {QuoteIdentifier(name.ColumnName)})
                VALUES (1, 'benchmark');
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteQueriesAsync(LargeModelContext context, int count)
    {
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            total += await QueryAccessors[i](context).ConfigureAwait(false);
        }

        return total;
    }

    private static void EnsureResult(int result, int expected, string phase)
    {
        if (result != expected)
            throw new InvalidOperationException($"{phase} returned {result} rows; expected {expected}.");
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

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
            .Where(value => value is "one" or "ten")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<bool> ReadBoolValues(string variable, params bool[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return defaults;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(bool.Parse)
            .Distinct()
            .ToArray();
    }

    private static readonly Func<LargeModelContext, Task<int>>[] QueryAccessors =
    [
        static context => context.Entity001.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity002.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity003.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity004.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity005.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity006.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity007.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity008.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity009.Where(row => row.Id == 1).CountAsync(),
        static context => context.Entity010.Where(row => row.Id == 1).CountAsync(),
    ];
}
