# Historical PostgreSQL large-model benchmark — 2026-11-01

Machine: Intel Core i7-12700, Windows 11, .NET 10.0.14; PostgreSQL 18.1 in a local Docker Desktop container. BenchmarkDotNet 0.14.0, one process launch per case. Timings include sequential `CountAsync` queries with one matching row, SQL translation, connection pool checkout, database round trips and scalar materialization. This is a local, single-threaded, warm-cache scenario; remote latency, concurrent load, startup, large results, and transaction behavior were not measured.

This file is a historical baseline captured before the production `DbContext` gained its instance-level model cache (and before the current DbSet cache/lease implementation). Do not read the `current model path` rows as evidence that today's path rebuilds an uncached model. The context contains source-generated metadata for 100 or 300 entities. Only the one or ten queried tables are physically created, each containing one row. `Fluent=true` applies a comment configuration to every entity; it is a deliberately heavy configuration stress case. The context-level model-cache candidate is benchmark-only historical control. The prewarmed concurrent-queue pool prototype does not reset state, manage leases, validate isolation, handle transactions, or measure contention; it is not a safe production pool. Pool allocations exclude retained instances. Use `DbContextPoolBenchmarks` for the real `AddDbContextPool` versus `AddDbContext` scope comparison. The runtime's managed allocation diagnostics for asynchronous operations should be interpreted cautiously.

Each benchmark validates results before timing and uses a randomly named schema that it cleans up. Times are noisy on this machine and should not be interpreted as precise production speedups. The historical uncached baseline cannot be reproduced against current code; the benchmark README describes how to run the fixture with the current implementation.

## Full matrix: six iterations
```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9711)
Unknown processor
.NET SDK 11.0.100-rc.3.26522.148
  [Host]     : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2
  Job-WVUYVW : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

IterationCount=6  IterationTime=200ms  LaunchCount=1
WarmupCount=3

```
| Method                                                       | ModelSize | AccessProfile | Fluent | Mean        | Error       | StdDev      | Median      | Ratio | RatioSD | Gen0     | Gen1     | Allocated  | Alloc Ratio |
|------------------------------------------------------------- |---------- |-------------- |------- |------------:|------------:|------------:|------------:|------:|--------:|---------:|---------:|-----------:|------------:|
| **&#39;PostgreSQL; new context; current model path&#39;**                | **100**       | **one**           | **False**  |    **985.9 μs** |  **1,220.6 μs** |   **435.26 μs** |    **889.7 μs** |  **1.18** |    **0.69** |        **-** |        **-** |    **6.53 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 100       | one           | False  |  1,024.7 μs |  1,066.0 μs |   276.85 μs |  1,070.3 μs |  1.23 |    0.58 |        - |        - |     6.4 KB |        0.98 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 100       | one           | False  |    851.3 μs |    615.6 μs |   219.54 μs |    762.2 μs |  1.02 |    0.47 |        - |        - |    5.16 KB |        0.79 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **100**       | **one**           | **True**   |  **1,050.0 μs** |  **1,321.1 μs** |   **471.10 μs** |    **972.5 μs** |  **1.19** |    **0.72** |  **25.0000** |   **4.1667** |   **323.4 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 100       | one           | True   |  1,144.6 μs |  1,303.7 μs |   464.90 μs |  1,210.8 μs |  1.30 |    0.74 |  12.5000 |        - |  164.87 KB |        0.51 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 100       | one           | True   |    931.6 μs |    916.3 μs |   326.75 μs |    815.9 μs |  1.06 |    0.56 |        - |        - |    5.13 KB |        0.02 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **100**       | **ten**           | **False**  | **10,288.0 μs** |  **8,642.6 μs** | **3,082.05 μs** | **10,719.7 μs** |  **1.11** |    **0.53** |        **-** |        **-** |   **52.83 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 100       | ten           | False  | 12,236.6 μs | 30,634.5 μs | 7,955.68 μs |  8,500.0 μs |  1.32 |    0.96 |        - |        - |   50.48 KB |        0.96 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 100       | ten           | False  | 10,187.5 μs |  2,626.3 μs |   936.56 μs |  9,997.8 μs |  1.10 |    0.42 |        - |        - |   47.59 KB |        0.90 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **100**       | **ten**           | **True**   | **16,484.9 μs** | **11,483.1 μs** | **4,094.97 μs** | **17,445.6 μs** |  **1.06** |    **0.38** | **250.0000** |        **-** | **3230.67 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 100       | ten           | True   | 10,004.5 μs |  7,177.0 μs | 1,863.85 μs | 10,412.2 μs |  0.64 |    0.21 |        - |        - |  208.84 KB |        0.06 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 100       | ten           | True   | 10,083.3 μs | 11,375.8 μs | 4,056.71 μs |  9,775.5 μs |  0.65 |    0.31 |        - |        - |    53.7 KB |        0.02 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **one**           | **False**  |    **950.0 μs** |  **1,210.4 μs** |   **431.65 μs** |    **938.1 μs** |  **1.21** |    **0.75** |        **-** |        **-** |    **6.51 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | one           | False  |    706.7 μs |    650.9 μs |   232.12 μs |    584.5 μs |  0.90 |    0.48 |        - |        - |    6.38 KB |        0.98 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | one           | False  |    872.9 μs |  1,290.6 μs |   460.23 μs |    687.1 μs |  1.11 |    0.75 |        - |        - |    5.17 KB |        0.79 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **one**           | **True**   |  **1,921.2 μs** |  **1,024.1 μs** |   **365.22 μs** |  **1,795.6 μs** |  **1.03** |    **0.25** |  **72.9167** |  **10.4167** |  **942.66 KB** |       **1.000** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | one           | True   |  1,104.9 μs |    305.1 μs |    79.22 μs |  1,068.0 μs |  0.59 |    0.10 |  31.2500 |   7.8125 |  474.87 KB |       0.504 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | one           | True   |    958.6 μs |  1,850.9 μs |   660.06 μs |    550.7 μs |  0.51 |    0.34 |        - |        - |    5.18 KB |       0.005 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **ten**           | **False**  |  **6,485.3 μs** |  **1,433.8 μs** |   **372.34 μs** |  **6,253.2 μs** |  **1.00** |    **0.07** |        **-** |        **-** |   **57.97 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | ten           | False  |  6,964.3 μs |  2,844.2 μs |   738.63 μs |  6,877.4 μs |  1.08 |    0.12 |        - |        - |   58.25 KB |        1.00 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | ten           | False  |  6,362.2 μs |  3,687.9 μs |   957.74 μs |  6,078.0 μs |  0.98 |    0.14 |        - |        - |   46.43 KB |        0.80 |
|                                                              |           |               |        |             |             |             |             |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **ten**           | **True**   | **25,877.8 μs** | **20,724.1 μs** | **5,381.98 μs** | **24,625.4 μs** |  **1.04** |    **0.29** | **714.2857** | **142.8571** | **9414.29 KB** |       **1.000** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | ten           | True   |  9,510.7 μs |  5,005.1 μs | 1,299.81 μs |  9,593.7 μs |  0.38 |    0.09 |        - |        - |  523.56 KB |       0.056 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | ten           | True   | 10,604.2 μs |  5,306.1 μs | 1,377.98 μs | 10,960.3 μs |  0.43 |    0.10 |        - |        - |   53.68 KB |       0.006 |


## Focused repeat: 300 entities, ten queries, twelve iterations

```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9711)
Unknown processor
.NET SDK 11.0.100-rc.3.26522.148
  [Host]     : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2
  Job-JEAFAH : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

IterationCount=12  IterationTime=500ms  LaunchCount=1
WarmupCount=5

```
| Method                                                       | ModelSize | AccessProfile | Fluent | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0     | Gen1     | Allocated  | Alloc Ratio |
|------------------------------------------------------------- |---------- |-------------- |------- |----------:|---------:|---------:|------:|--------:|---------:|---------:|-----------:|------------:|
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **ten**           | **False**  |  **9.219 ms** | **3.369 ms** | **2.631 ms** |  **1.09** |    **0.45** |        **-** |        **-** |   **52.86 KB** |        **1.00** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | ten           | False  |  7.209 ms | 2.620 ms | 1.894 ms |  0.85 |    0.34 |        - |        - |   50.47 KB |        0.95 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | ten           | False  |  8.688 ms | 3.325 ms | 2.596 ms |  1.03 |    0.44 |        - |        - |    46.2 KB |        0.87 |
|                                                              |           |               |        |           |          |          |       |         |          |          |            |             |
| **&#39;PostgreSQL; new context; current model path&#39;**                | **300**       | **ten**           | **True**   | **12.814 ms** | **2.378 ms** | **1.719 ms** |  **1.02** |    **0.19** | **736.8421** | **210.5263** | **9405.73 KB** |       **1.000** |
| &#39;PostgreSQL; new context; per-context model cache candidate&#39; | 300       | ten           | True   | 11.894 ms | 8.094 ms | 6.319 ms |  0.94 |    0.50 |  20.8333 |        - |  517.94 KB |       0.055 |
| &#39;PostgreSQL; prototype pool rent/return; no reset&#39;           | 300       | ten           | True   |  9.004 ms | 3.517 ms | 2.746 ms |  0.71 |    0.23 |        - |        - |   46.31 KB |       0.005 |
