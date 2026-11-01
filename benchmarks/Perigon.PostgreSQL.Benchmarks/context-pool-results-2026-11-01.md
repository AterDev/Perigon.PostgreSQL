# Production DI context-pool microbenchmark — 2026-11-01

This is a local single-threaded BenchmarkDotNet microbenchmark on an Intel Core i7-12700, Windows 11 and .NET 10.0.14, with one process launch, three warmups and six 200 ms measurement iterations per case. Each measured operation creates and disposes a DI scope, resolves a context and accesses one or ten different DbSets twice. Both paths use the same shared NpgsqlDataSource and source-generated 100/300 entity models; no connection is opened, SQL is generated or database is contacted. Model configuration has no Fluent entity overrides. The pool path exercises the actual AddDbContextPool reset, DbSet invalidation and lease handling; its retained idle instance memory is not reflected in per-operation allocation. The benchmark measures steady-state scope and metadata overhead, not network latency, request throughput, contention, or end-to-end behavior.

Run using the command in README.md; this report used PERIGON_POOL_MODEL_SIZES=100,300, PERIGON_POOL_ACCESSES=one,ten and PERIGON_POOL_SIZES=1.
```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9711)
Unknown processor
.NET SDK 11.0.100-rc.3.26522.148
  [Host]     : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2
  Job-ZAKMUJ : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

IterationCount=6  IterationTime=200ms  LaunchCount=1
WarmupCount=3

```
| Method                       | ModelSize | AccessProfile | PoolSize | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------------- |---------- |-------------- |--------- |-----------:|----------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| **&#39;DI scope; AddDbContext&#39;**     | **100**       | **one**           | **1**        | **1,173.2 ns** | **146.34 ns** |  **38.00 ns** |  **1.00** |    **0.04** | **0.3714** |      **-** |    **4888 B** |        **1.00** |
| &#39;DI scope; AddDbContextPool&#39; | 100       | one           | 1        |   288.0 ns |  38.99 ns |  13.90 ns |  0.25 |    0.01 | 0.0524 |      - |     688 B |        0.14 |
|                              |           |               |          |            |           |           |       |         |        |        |           |             |
| **&#39;DI scope; AddDbContext&#39;**     | **100**       | **ten**           | **1**        | **2,183.7 ns** | **582.73 ns** | **207.81 ns** |  **1.01** |    **0.12** | **0.5993** | **0.0105** |    **7896 B** |        **1.00** |
| &#39;DI scope; AddDbContextPool&#39; | 100       | ten           | 1        |   905.5 ns | 145.97 ns |  52.05 ns |  0.42 |    0.04 | 0.2212 |      - |    2920 B |        0.37 |
|                              |           |               |          |            |           |           |       |         |        |        |           |             |
| **&#39;DI scope; AddDbContext&#39;**     | **300**       | **one**           | **1**        | **2,881.1 ns** | **559.80 ns** | **199.63 ns** |  **1.00** |    **0.09** | **0.8888** | **0.0269** |   **11776 B** |        **1.00** |
| &#39;DI scope; AddDbContextPool&#39; | 300       | one           | 1        |   307.6 ns |  67.47 ns |  24.06 ns |  0.11 |    0.01 | 0.0523 |      - |     688 B |        0.06 |
|                              |           |               |          |            |           |           |       |         |        |        |           |             |
| **&#39;DI scope; AddDbContext&#39;**     | **300**       | **ten**           | **1**        | **3,942.8 ns** | **600.87 ns** | **214.28 ns** |  **1.00** |    **0.07** | **1.1293** | **0.0411** |   **14784 B** |        **1.00** |
| &#39;DI scope; AddDbContextPool&#39; | 300       | ten           | 1        | 1,085.3 ns | 123.87 ns |  44.17 ns |  0.28 |    0.02 | 0.2217 |      - |    2920 B |        0.20 |
