using BenchmarkDotNet.Running;
using Perigon.PostgreSQL.Benchmarks;

BenchmarkSwitcher.FromTypes(new[] { typeof(QueryBenchmarks), typeof(TranslationBenchmarks) }).Run(args);
