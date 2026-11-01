# Query benchmark

## Historical large-model context benchmark

The checked-in results of `LargeModelBenchmarks` and `LargeModelDatabaseBenchmarks`
are historical, captured before the production `DbContext` gained instance-level model caching (and
before the current DbSet cache/lease implementation). Their `current model path`
rows therefore must not be read as evidence that the current path has no cache. The
legacy per-context cache flag is now redundant; rerunning these fixtures against
current code will not reproduce the historical uncached baseline.

The old prewarmed-context and concurrent-queue rows are optimistic historical
performance bounds. They do not implement the production pool's reset, lease
validation, disposal, isolation, or contention semantics, and are not evidence that
the current implementation is safe or that it has the same performance. The real
DI pool comparison is the independent `DbContextPoolBenchmarks` section below.

The benchmark uses source-generated models for 100 or 300 entities and accesses
one, ten, or all sets with or without Fluent configuration. It shares one Npgsql
data source and does not open a database connection. Select scenarios with:

```powershell
$env:PERIGON_MODEL_CACHE_SIZES = "100,300"
$env:PERIGON_MODEL_CACHE_ACCESSES = "one,ten,all"
$env:PERIGON_MODEL_CACHE_FLUENT = "false,true"
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks -- --filter '*LargeModelBenchmarks*' --job short
```

This measures synchronous model/set lookup and context lifetime in isolation;
it does not measure cold application startup, network, PostgreSQL execution,
end-to-end request latency, or the production DI pool.

`LargeModelDatabaseBenchmarks` uses the same 100/300 generated-entity fixture but
executes one or ten separate `CountAsync` queries against PostgreSQL. It creates
only the one or ten queried tables in a randomly named schema in the configured
disposable database, drops that schema after the run, warms each historical path,
and checks the result before timing. These rows are historical single-threaded
database measurements, not a comparison of the current pool implementation. Use a database
account allowed to create and drop schemas, and set its connection string locally:

```powershell
# Set PERIGON_BENCHMARK_POSTGRES_CONNECTION_STRING in your local environment first.
$env:PERIGON_LIVE_MODEL_SIZES = "100,300"
$env:PERIGON_LIVE_MODEL_ACCESSES = "one,ten"
$env:PERIGON_LIVE_MODEL_FLUENT = "false,true"
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks -- --filter '*LargeModelDatabaseBenchmarks*' --job short
```

These are sequential local-database measurements, not concurrent throughput,
remote-network latency, or an implementation of the current DI context pool.

## DI context pool benchmark

`DbContextPoolBenchmarks` compares the real production registrations
`AddDbContext<TContext>` and `AddDbContextPool<TContext>` under the same DI scope
lifecycle. It does not use the old queue prototype. Each measured operation creates
and disposes a scope, resolves a context, and accesses one or ten expression-bodied
DbSet properties twice; the second read exercises the per-context DbSet cache. The
pool path includes the production lease, reset, and return flow.

The benchmark reuses the existing source-generated entities and registers a 100- or
300-entity model for a benchmark-only context with exactly one public
`DbContextOptions<TContext>` constructor. It only touches metadata and DbSets, so no
database connection is opened. Both paths receive the same stable external
`NpgsqlDataSource`; two warmup scopes with the same access profile run before timing.
`PoolSize` can be selected with `PERIGON_POOL_SIZES` (default `1,32`), and model/access
parameters with `PERIGON_POOL_MODEL_SIZES` and `PERIGON_POOL_ACCESSES`:

```powershell
$env:PERIGON_POOL_MODEL_SIZES = "100,300"
$env:PERIGON_POOL_ACCESSES = "one,ten"
$env:PERIGON_POOL_SIZES = "1,32"
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks -- --filter '*DbContextPoolBenchmarks*' --job short
```

This is intentionally a scoped-lifecycle microbenchmark, not concurrent request
throughput. Do not include the historical queue rows when assessing the production
pool. The local 100/300-entity run is recorded in
[context-pool-results-2026-11-01.md](context-pool-results-2026-11-01.md).

## Database query benchmark

This project compares one equivalent PostgreSQL query through Perigon.PostgreSQL,
EF Core with `AsNoTracking`, Dapper, and handwritten Npgsql. All four methods use
the same `NpgsqlDataSource` and connection pool settings, return the same five
columns, and materialize the same requested result (one row, 100 rows, and
10,000 rows). The benchmark reports elapsed time and allocations with
BenchmarkDotNet. It also measures Perigon's SQL translation for a repeated query
shape separately, with two parameter values, so translation-cache changes can be
verified without database/network noise.

The setup creates a new schema with a unique suffix and a
`benchmark_users` table containing 100,000 deterministic rows. The schema is
dropped during normal benchmark cleanup, so existing schemas and tables are not
touched. Use a disposable PostgreSQL database and optionally set the schema
prefix before running:

```powershell
$env:PERIGON_BENCHMARK_POSTGRES_CONNECTION_STRING = "Host=localhost;Port=5432;Database=perigon_benchmark;Username=postgres;Password=postgres"
$env:PERIGON_BENCHMARK_SCHEMA = "perigon_benchmark"
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks
```

The generated report is written under `BenchmarkDotNet.Artifacts/results`.
The benchmark includes a warm-up call for each provider; it measures steady-state
query, network, and materialization cost. The provider methods are checked for
row-for-row equality before timing. Run a single benchmark method when iterating
on translation, for example:

```powershell
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks -- --filter '*TranslationBenchmarks*'
```

The full matrix includes `MinAge=20,40` and `Limit=1,100,10000`; it can take
several minutes. Set `PERIGON_BENCHMARK_MIN_AGE` and
`PERIGON_BENCHMARK_LIMIT` to comma-separated positive integers to select a
smaller scenario, for example:

```powershell
$env:PERIGON_BENCHMARK_MIN_AGE = "20"
$env:PERIGON_BENCHMARK_LIMIT = "10000"
dotnet run -c Release --project benchmarks/Perigon.PostgreSQL.Benchmarks -- --filter '*QueryBenchmarks*'
```

`TranslationBenchmarks` measures rebuilding an `IQueryable`, reusing one query
object, and reusing it while changing a captured value on every call. It does
not open a database connection; the four-provider query benchmarks do. Neither
benchmark claims to measure startup, schema creation, migrations, or a different
database/server configuration.

The structural cache currently covers simple integer comparisons with an
optional leading boolean condition, ordering and `Take` with an integer value.
Other query shapes use the existing translator. Compare equivalent scenarios
on the same machine; the translation benchmark is not a general ORM ranking.
