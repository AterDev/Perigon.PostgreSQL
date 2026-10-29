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

## 关系与 Include

关系元数据可以使用命名约定、`[ForeignKey]` 或 Fluent API 配置。源生成器会为同一
`DbContext` 中可静态识别的单列关系注册 AOT 安全的导航 loader；导航属性应标记
`[NotMapped]`，集合导航当前使用 `List<T>`，并且必须有公开 setter。

```csharp
public sealed class Blog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [NotMapped]
    public User? User { get; set; }
}

public sealed class User
{
    public int Id { get; set; }
    [NotMapped]
    public List<Blog> Blogs { get; set; } = [];
}

var users = await db.Users
    .Include(user => user.Blogs.Where(blog => blog.Id > 0)
        .OrderBy(blog => blog.Id).Take(10))
    .ToListAsync();
```

`Include` 目前是单层 split query，支持直接引用或一对多集合的筛选、排序和分页；
`ThenInclude`、多对多导航自动填充、复合键导航 loader 和 `AsSingleQuery` 尚未支持。
显式多对多关系可通过 `HasMany().WithMany().UsingEntity<TJoin>(...)` 生成两条外键，
或使用 `IncludeAsync`/`IncludeManyToManyAsync` 通过键 selector 组装结果。仅在
`OnModelCreating` 中配置且无法被源生成器识别的关系仍可用于 DDL，但不会自动生成
导航 Include loader。

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

`AddDbContext` 会在容器中按生效的连接字符串复用单例 `NpgsqlDataSource`，并在容器释放时释放它。这样每个 scoped `DbContext` 不会重复创建和销毁连接池。多租户场景如果每个请求都会产生新的连接字符串，会让数据源缓存随连接字符串数量增长；此时应在应用层按租户管理有限数量的 `NpgsqlDataSource`，再通过 `UseNpgsql(dataSource)` 注入，而不是为每个请求拼接新的连接字符串。

直接使用 `new AppDbContext(connectionString)` 仍由上下文自行创建和释放数据源，适合短期工具、迁移和脚本。长时间运行的服务应注册并共享一个 `NpgsqlDataSource`，或使用上面的 DI 注册方式。

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
