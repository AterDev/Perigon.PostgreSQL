# 本地测试与覆盖率

当前暂不启用 GitHub Actions 测试流程，构建、测试、覆盖率和 NativeAOT 验证均在本地执行。

## 基础验证

```powershell
dotnet restore .\Perigon.PostgreSQL.slnx -p:NuGetAuditMode=direct -warnaserror:NU1900
dotnet build .\Perigon.PostgreSQL.slnx --no-restore
dotnet test .\tests\Perigon.PostgreSQL.Tests\Perigon.PostgreSQL.Tests.csproj --no-build --no-restore
dotnet test .\tests\Perigon.PostgreSQL.IntegrationTests\Perigon.PostgreSQL.IntegrationTests.csproj --no-build --no-restore
```

集成测试默认使用固定的 PostgreSQL 18.1 Alpine 镜像；也可以通过外部连接字符串运行，并由测试夹具负责随机 schema 隔离和安全清理。

## 覆盖率

覆盖率配置位于 `.config/coverage.runsettings`。`scripts/merge-coverage.ps1` 不依赖 ReportGenerator 或额外 action：它读取 unit/integration 的 `coverage.cobertura.xml`，合并报告并执行门禁：

- 合并行覆盖率 >= 75%；
- 合并分支覆盖率 >= 60%；
- 核心库行覆盖率 >= 75%、分支覆盖率 >= 60%；
- SourceGeneration 行覆盖率 >= 90%、分支覆盖率 >= 70%；
- 只有显式传入 `-DiffBase` 时，才检查新增/修改代码的行覆盖率 >= 80%。

```powershell
dotnet test .\tests\Perigon.PostgreSQL.Tests\Perigon.PostgreSQL.Tests.csproj --collect:"XPlat Code Coverage" --settings .\.config\coverage.runsettings --results-directory .\TestResults\unit
dotnet test .\tests\Perigon.PostgreSQL.IntegrationTests\Perigon.PostgreSQL.IntegrationTests.csproj --collect:"XPlat Code Coverage" --settings .\.config\coverage.runsettings --results-directory .\TestResults\integration
pwsh .\scripts\merge-coverage.ps1 -InputPath .\TestResults -OutputPath .\TestResults\coverage\merged.cobertura.xml
```

分支覆盖率按 Cobertura 行级 `condition-coverage` 合并；相同源行跨报告取较高覆盖值，因此重复运行不会重复计数。

## 其他本地验证

- NativeAOT：发布并运行 `win-x64` 或 `linux-x64` smoke 项目；`linux-arm64` 可在发布环境中验证。
- 工具包：执行核心包和 `Perigon.PostgreSQL.Tools` 的 pack/install smoke。
- 性能：本地运行 `benchmarks/Perigon.PostgreSQL.Benchmarks` 的短基线。
