# Query benchmark

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
