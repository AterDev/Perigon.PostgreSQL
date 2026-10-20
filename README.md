# Perigon.PostgreSQL

本库主要是为了满足AOT的需要，并保持 EF Core 的 `DbContext` / `DbSet<T>` 使用方式，专门面向小型应用场景的 PostgreSQL ORM，它支持大部分常见的LINQ查询、原生SQL、事务等功能，但不包含变更跟踪，SaveChanges等机制。

## 安装

```powershell
dotnet add package Perigon.PostgreSQL
```

## 定义实体

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("users", Schema = "app")]
public sealed class User
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")]
    public int Id { get; set; }

    [Column("user_name")]
    public string UserName { get; set; } = "";

    [Column("tags", TypeName = "text[]")]
    public string[] Tags { get; set; } = [];

    [Column("profile_json", TypeName = "jsonb")]
    public string? ProfileJson { get; set; }
}
```

## 定义 `DbContext`

```csharp
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Options;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public AppDbContext(string connectionString)
        : base(builder => builder.UseNpgsql(connectionString))
    {
    }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users", "app");
            entity.Property(x => x.UserName).HasColumnName("user_name").IsRequired();
            entity.HasIndex(x => x.UserName)
                .HasDatabaseName("uq_users_user_name")
                .IsUnique();
        });
    }
}
```

## 创建数据库对象

如果你已经定义好了模型和上下文，可以直接创建 schema / table / foreign key / index：

```csharp
await using var db = new AppDbContext(connectionString);
await db.EnsureCreatedAsync();
```

## 查询与写入

```csharp
await using var db = new AppDbContext(connectionString);

var users = await db.Users
    .Where(x => x.Tags.Contains("postgres"))
    .OrderBy(x => x.UserName)
    .ToListAsync();

var inserted = await db.Users.InsertAsync(new User
{
    UserName = "alice",
    Tags = ["developer", "postgres"],
    ProfileJson = """{"level":3}"""
});
```

## 从现有 PostgreSQL 数据库反向生成代码

安装工具：

```powershell
dotnet tool install --global Perigon.PostgreSQL.Tools
```

最简单的用法：

```powershell
dotnet perigon database scaffold --connection "Host=localhost;Database=app;Username=postgres;Password=postgres"
```

默认行为：

- `--context` 可省略，默认 `DefaultDbContext`
- `--namespace` 可省略，默认 `AppDbContext`
- `--output` 可省略，默认当前目录 `.`
- `DbContext` 文件默认输出到 `AppDbContext/<ContextName>.cs`
- 实体默认输出到 `Entity/*.cs`
- 实体命名空间默认是 `AppDbContext.Entity`
- 默认包含视图；如果不需要，使用 `--no-views`

常见示例：

```powershell
dotnet perigon database scaffold `
    --connection "Host=localhost;Database=app;Username=postgres;Password=postgres" `
    --context MyDbContext `
    --namespace MyApp.Data `
    --output .\Generated `
    --schema public `
    --schema audit
```

可用参数：

- `--connection` 必填
- `--context`
- `--namespace`
- `--output`
- `--schema`（可重复）
- `--table`（可重复）
- `--force`
- `--dry-run`
- `--no-views`

如果你正在这个仓库里开发，也可以直接运行工具项目：

```powershell
dotnet run --project .\src\Perigon.PostgreSQL.Tools\Perigon.PostgreSQL.Tools.csproj -- database scaffold --connection "Host=localhost;Database=app;Username=postgres;Password=postgres"
```

## 增量迁移

`EnsureCreatedAsync` 适用于新数据库的初始建表。已有数据库上线后，使用模型快照和版本控制中的迁移文件升级结构：

```powershell
dotnet perigon migrations add AddUserDisplayName --project .\src\App\App.csproj --context AppDbContext
dotnet perigon migrations list --project .\src\App\App.csproj --context AppDbContext
dotnet perigon migrations script --project .\src\App\App.csproj --from 0 --to <migration-id> --idempotent
dotnet perigon database update --project .\src\App\App.csproj --context AppDbContext --connection $connectionString
```

`migrations add` 会构建项目，运行当前上下文配置并生成 `Up`、`Down` 和新的 model snapshot。请先审查并提交生成文件，再对目标数据库运行 `database update`。设计时上下文需要 public 无参构造函数或单 `string connectionString` 构造函数；省略 `--context` 时项目必须恰好有一个 `DbContext`。多目标框架项目使用 `--framework` 指定目标。

已通过 `EnsureCreated` 创建的数据库，可在项目只有一个初始迁移时运行 `database baseline`。它会核对快照中的受管表结构，匹配后记录初始 migration，不会重建表。增量迁移和 `EnsureCreated` 不应混用。

运行时也可以调用 `await db.MigrateAsync()`，或通过 `await db.MigrateAsync("<migration-id>")` 指定目标版本；目标为 `"0"` 时回滚全部迁移。history 默认存于 `public."_PerigonMigrationsHistory"`，可使用 `UseMigrationsHistoryTable(tableName, schema)` 配置。每条迁移的 DDL 与 history 更新在同一事务中执行，并使用 PostgreSQL advisory lock 串行化。

若配置了自定义 history 表，生成 SQL 脚本时传入相同的 `--history-schema` 和 `--history-table`。幂等脚本将每个 `MigrationBuilder.Sql` 命令作为一条 SQL 执行；自定义 SQL 请拆成单条命令。

迁移 diff 支持 schema、表列、类型、nullability、identity、default、主外键、索引和注释。删除表列、修改类型或收紧 nullability 会提示数据风险；rename、数据回填和复杂转换需要手工编辑迁移并提供 `Down`。视图定义、表达式索引、CHECK/EXCLUDE 约束和触发器等不在 v1 自动迁移范围内。

## ASP.NET Core 注册

```csharp
using Perigon.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")!));
```

## 示例项目

示例项目位于：

```text
samples/Perigon.PostgreSQL.AspNetCoreSample
```

启动示例数据库：

```powershell
cd .\samples\Perigon.PostgreSQL.AspNetCoreSample
docker compose up -d
```

运行示例：

```powershell
dotnet run --project .\samples\Perigon.PostgreSQL.AspNetCoreSample\Perigon.PostgreSQL.AspNetCoreSample.csproj --urls http://localhost:5088
```
