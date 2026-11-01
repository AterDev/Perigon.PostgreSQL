using BenchmarkDotNet.Running;
using Perigon.PostgreSQL.Benchmarks;

BenchmarkSwitcher.FromTypes(new[]
{
    typeof(QueryBenchmarks),
    typeof(TranslationBenchmarks),
    typeof(LargeModelBenchmarks),
    typeof(LargeModelDatabaseBenchmarks),
    typeof(DbContextPoolBenchmarks)
}).Run(args);
