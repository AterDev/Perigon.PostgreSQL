# Perigon.PostgreSQL.Tools

`Perigon.PostgreSQL.Tools` 是 Perigon.PostgreSQL 的 .NET 命令行工具，命令名为 `dotnet perigon`。它提供两类功能：从 PostgreSQL 数据库反向生成实体和 `DbContext`，以及为应用项目生成、查看、导出和应用数据库迁移。

## 安装

需要 .NET 10 SDK 和运行时。全局安装：

```powershell
dotnet tool install --global Perigon.PostgreSQL.Tools
```

升级：

```powershell
dotnet tool update --global Perigon.PostgreSQL.Tools
```

安装后使用 `dotnet perigon`。不带有效命令运行时，工具会打印命令用法。

## 从数据库生成代码

`database scaffold` 连接现有 PostgreSQL 数据库，生成 `DbContext` 和实体类：

```powershell
dotnet perigon database scaffold `
    --connection "Host=localhost;Database=app;Username=postgres;Password=postgres" `
    --context AppDbContext `
    --namespace MyApp.Data `
    --output .\Generated `
    --schema public
```

命令会在输出目录下生成 `AppDbContext/<ContextName>.cs` 和 `Entity/*.cs`。默认包含视图；使用 `--no-views` 排除视图。

| 参数 | 作用 | 默认值 |
| --- | --- | --- |
| `--connection` | PostgreSQL 连接字符串，必填 | — |
| `--context` | 生成的 `DbContext` 名称 | `DefaultDbContext` |
| `--namespace` | 生成代码的根命名空间 | `AppDbContext` |
| `--output` | 输出目录 | 当前目录 |
| `--schema` | 只处理指定 schema，可重复 | 所有 schema |
| `--table` | 只处理指定表，可重复 | 所有表 |
| `--no-views` | 不为视图生成实体 | 默认包含视图 |
| `--force` | 覆盖同名已存在文件 | 不覆盖 |
| `--dry-run` | 只打印将生成的文件路径 | 写入文件 |

连接字符串也可以从环境变量传入，避免直接写在命令历史中：

```powershell
$connection = $env:PERIGON_CONNECTION
dotnet perigon database scaffold --connection $connection --output .\Generated
```

## 增量迁移

迁移命令会构建 `--project` 指定的应用项目并加载其中的 `DbContext`。设计时上下文需要公开的无参构造函数，或接收单个连接字符串的公开构造函数。省略 `--context` 时，项目必须只有一个 `DbContext`；多目标框架项目可通过 `--framework` 选择目标框架，构建配置可通过 `--configuration` 指定。

### 生成并检查迁移

模型变化后生成迁移和模型快照：

```powershell
dotnet perigon migrations add AddUserDisplayName `
    --project .\src\App\App.csproj `
    --context AppDbContext

dotnet perigon migrations list `
    --project .\src\App\App.csproj `
    --context AppDbContext
```

`migrations add` 默认将迁移文件和快照写入项目目录下的 `Migrations` 文件夹，也可以用 `--output` 指定目录、用 `--namespace` 指定命名空间。生成的 C# 迁移包含 `Up` 和 `Down`。请先检查差异和数据风险提示，必要时手工处理列重命名、数据回填或类型转换，并将迁移文件和快照纳入版本控制。

### 生成 SQL 脚本

```powershell
dotnet perigon migrations script `
    --project .\src\App\App.csproj `
    --from 0 `
    --to <migration-id> `
    --idempotent `
    --output .\migration.sql
```

省略 `--from` 时从 `0`（尚未应用迁移）开始；省略 `--to` 时使用最新迁移。`--idempotent` 会按迁移 history 状态决定是否执行。将 `--from` 设为当前迁移、`--to` 设为较早的迁移可生成回滚脚本。默认 history 表为 `public."_PerigonMigrationsHistory"`；自定义 history 表时，给脚本命令同时传入 `--history-schema <schema>` 和 `--history-table <table>`。

### 应用或回滚迁移

```powershell
$connection = $env:PERIGON_CONNECTION
dotnet perigon database update `
    --project .\src\App\App.csproj `
    --context AppDbContext `
    --connection $connection
```

默认应用所有未执行的迁移。通过 `--target <migration-id>` 指定目标版本；`--target 0` 回滚所有已应用迁移。每条迁移的 DDL 与 history 记录在同一事务中提交，并使用 PostgreSQL advisory lock 串行化并发迁移。

### 已有数据库建立基线

如果数据库已经由 `EnsureCreated` 创建，可在迁移系统中登记初始迁移，而不重复建表：

```powershell
dotnet perigon database baseline `
    --project .\src\App\App.csproj `
    --context AppDbContext `
    --connection $connection
```

基线命令会核对初始迁移快照中的受管结构，匹配后写入 history。当前要求项目只注册一个初始迁移。`EnsureCreated` 用于初始化数据库，不负责应用后续增量迁移；建立基线后使用 `database update` 管理结构变更。

## 命令速查

```text
dotnet perigon database scaffold --connection <connection-string> [--context <name>] [--namespace <namespace>] [--output <directory>] [--schema <schema>] [--table <table>] [--force] [--dry-run] [--no-views]
dotnet perigon migrations add <name> --project <project.csproj> [--context <name>] [--output <directory>] [--namespace <namespace>]
dotnet perigon migrations list --project <project.csproj> [--context <name>]
dotnet perigon migrations script --project <project.csproj> [--from <id|0>] [--to <id|0>] [--idempotent] [--output <file>]
dotnet perigon database update --project <project.csproj> --connection <connection-string> [--context <name>] [--target <id|0>]
dotnet perigon database baseline --project <project.csproj> --connection <connection-string> [--context <name>] [--migration <id>]
```

更多迁移范围和限制见[迁移文档](https://github.com/AterDev/Perigon.PostgreSQL/blob/master/docs/06-%E6%95%B0%E6%8D%AE%E5%BA%93%E8%BF%81%E7%A7%BB.md)。
