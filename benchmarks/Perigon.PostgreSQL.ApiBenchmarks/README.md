# Perigon PostgreSQL API context-pool benchmark

这是一个独立的、可复现的 HTTP 基准工具。它不会修改主库的测试数据：每次运行都会创建类似 `api_bench_<随机后缀>` 的临时 schema，运行结束后执行 `DROP SCHEMA ... CASCADE`。

工具会为每个场景启动一个隐藏监听的 Kestrel 实例（`127.0.0.1` 随机端口），通过并发 `HttpClient` 请求同一个真实 PostgreSQL。`scoped` 使用 `AddDbContext`，`pooled` 使用 `AddDbContextPool`；两个模式使用同一个 `NpgsqlDataSource`，因此连接池因素保持一致，比较重点是 DbContext 生命周期/池化。

每个场景先以目标并发量预热并校验所有租户，再计时并发请求。计时请求会校验返回租户、行数、名称和上下文进入请求时的初始状态；另有 `/state` 探针验证上一请求设置的 `TenantId` 是否被 `OnPoolReturn()` 清除。HTTP 客户端直连回环地址，不使用系统代理。输出包括吞吐、平均延迟、P95/P99、错误数、租户串读数和状态泄漏数。

## 运行

工具默认连接 `127.0.0.1:5432`。例如 Docker PostgreSQL 使用本仓库测试容器的 `13484` 端口时：

```powershell
$env:PERIGON_API_BENCHMARK_POSTGRES_CONNECTION_STRING = 'Host=127.0.0.1;Port=13484;Database=perigon_benchmark;Username=postgres;Password=perigon_benchmark'
$env:PERIGON_API_MODEL_SIZES = '100,300'
$env:PERIGON_API_FLUENT_VALUES = 'false,true'
$env:PERIGON_API_BENCHMARK_MODES = 'scoped,pooled'
$env:PERIGON_API_CONCURRENCY = '32'
$env:PERIGON_API_REQUESTS = '256'
dotnet run --project .\benchmarks\Perigon.PostgreSQL.ApiBenchmarks\Perigon.PostgreSQL.ApiBenchmarks.csproj -c Release --no-launch-profile
```

首次运行需要正常的 NuGet restore；该项目本身不依赖 BenchmarkDotNet，也不会自动拉起或删除 Docker 容器。为了快速验证可先使用：

```powershell
$env:PERIGON_API_MODEL_SIZES = '100'
$env:PERIGON_API_FLUENT_VALUES = 'false'
$env:PERIGON_API_REQUESTS = '64'
```

可调环境变量：`PERIGON_API_WARMUP`（默认 32）、`PERIGON_API_TENANTS`（默认 8）、`PERIGON_API_CONTEXT_POOL_SIZE`（默认 `max(concurrency,16)`）、`PERIGON_API_NPGSQL_POOL_SIZE`（默认 128）。

### 输出示例

每个场景输出一行可读结果和一行 JSON：

```text
RESULT mode=pooled model=300 fluent=True requests=256 concurrency=32 throughput=.../s avg=...ms p95=...ms p99=...ms errors=0 tenantErrors=0 stateLeaks=0
```

模型文件中的 300 个实体由 Perigon.PostgreSQL source generator 生成；`100` 场景选择前 100 个模型，`300` 场景选择全部模型。请求只查询 `ApiEntity001`，但每次上下文首次构建仍处理所选模型列表，从而把大模型初始化成本纳入真实 API 生命周期比较。`Fluent=true` 对该实体应用一项表映射配置，并使用不同测试数据验证实际映射；不是对每个实体都配置 Fluent 的压力场景。

本机 Docker 测量和限制见 [results-2026-11-01.md](results-2026-11-01.md)。
