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

    public Guid TenantId { get; set; }

    public bool IsDeleted { get; set; }

    [Column("tags", TypeName = "text[]")]
    public string[] Tags { get; set; } = [];

    [Column("profile_json", TypeName = "jsonb")]
    public string? ProfileJson { get; set; }
}
```

单列 Guid 主键在结构化插入路径中使用客户端生成的版本 7 UUID：当主键仍为
`Guid.Empty` 时，库会在发送 INSERT 前赋值 `Guid.CreateVersion7()`；调用方已经提供的
非默认 Guid 会保留。这个行为不依赖变更追踪。

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

### 全局查询过滤器

全局过滤器在 `OnModelCreating` 中以名称注册。过滤表达式保留为表达式树，随普通
LINQ 查询翻译为 SQL，不会在客户端执行；同一实体可以注册多个过滤器。

```csharp
public sealed class AppDbContext : DbContext
{
    private readonly Guid _tenantId;

    public AppDbContext(string connectionString, Guid tenantId)
        : base(builder => builder.UseNpgsql(connectionString))
    {
        _tenantId = tenantId;
    }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        HasQueryFilter("Tenant", (User user) => user.TenantId == _tenantId);
        HasQueryFilter("NotDeleted", (User user) => !user.IsDeleted);
    }
}
```

`Set<T>()` 创建的查询默认应用全部过滤器。需要明确跳过部分具名过滤器时，使用
`SetWithoutFilters<T>("FilterName", ...)`；需要跳过全部过滤器时，使用
`SetIgnoringAllFilters<T>()`。具名 API 会校验过滤器名称，空列表或未知名称会抛出异常，
避免因拼写错误意外扩大查询范围：

```csharp
var tenantIncludingDeleted = await db
    .SetWithoutFilters<User>("NotDeleted")
    .Where(user => user.Id == userId)
    .SingleOrDefaultAsync();

var allTenants = await db.SetIgnoringAllFilters<User>().ToListAsync();
```

使用 `IQueryFilterContext.PushIgnoredFilters(...)` 建立临时过滤器范围时，受该范围影响而创建的查询必须在范围内执行；离开范围后继续使用会抛出异常。嵌套范围会合并需要忽略的名称。显式调用 `SetIgnoringAllFilters<T>()` 创建的查询不受临时范围的生命周期限制。

过滤器只约束查询，不替代写入授权：使用 `InsertAsync`、`BulkInsertAsync` 或其他写入
API 时，租户字段的赋值与校验由应用负责。`SqlQuery`、`SqlCommand` 和其他原生 SQL
不会自动追加全局过滤条件，必须在 SQL 中自行写出租户、软删除等条件。

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

### 自动时间字段

可以使用特性或 Fluent API 配置客户端 UTC 时间赋值：

```csharp
using Perigon.PostgreSQL.Attributes;

public sealed class User
{
    public Guid Id { get; set; }

    [AutoTimestamp(onInsert: true)]
    public DateTimeOffset CreatedAt { get; set; }

    [AutoTimestamp(onInsert: false, onUpdate: true)]
    public DateTimeOffset UpdatedAt { get; set; }
}
```

等价的 Fluent 配置为：

```csharp
modelBuilder.Entity<User>(entity =>
{
    entity.Property(user => user.CreatedAt).HasAutoTimestamp(onInsert: true);
    entity.Property(user => user.UpdatedAt).HasAutoTimestamp(onInsert: false, onUpdate: true);
});
```

插入时，只有默认值时间字段会被填充；调用方已经设置的时间会保留。更新时，配置了
`onUpdate` 的字段会在 `ExecuteUpdateAsync` 中自动使用当前 UTC 时间，除非同一次更新显式
设置了该字段。赋值发生在客户端，不会启用变更追踪。直接使用 `SqlCommand`、`SqlQuery`
或其他原生 SQL 不经过该策略，原生 SQL 需要自行维护时间和主键值。

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
多对多关系必须定义显式中间实体。`IncludeManyToManyAsync` 通过父实体、链接实体和子
实体的 `DbSet` 进行三次查询；写入和删除也必须直接操作链接实体的 `DbSet`。库不提供
通过父子导航隐式添加或删除多对多关系的操作。`HasMany().WithMany().UsingEntity<TJoin>(...)`
只用于关系元数据和 DDL，不能替代中间表写入。仅在 `OnModelCreating` 中配置且无法被
源生成器识别的关系仍可用于 DDL，但不会自动生成导航 Include loader。

## 事务

事务默认使用 PostgreSQL 的 `ReadCommitted` 隔离级别，也可以显式选择隔离级别：

```csharp
using System.Data;

await db.TransactionAsync(
    async ct =>
    {
        await db.Users.InsertAsync(user, ct);
        await db.Blogs.InsertAsync(blog, ct);
    },
    new TransactionOptions { IsolationLevel = IsolationLevel.Serializable },
    cancellationToken: cancellationToken);
```

事务不支持嵌套，也不会自动重试。相同 `DbContext` 中的内部写入会复用外层事务，操作
必须按顺序等待；需要并行工作时应使用不同的 `DbContext`。取消令牌控制连接打开、事务
action 和其中的 SQL 命令。action 正常返回后，提交阶段不再响应调用方取消，以避免提交
等待期间的取消造成误判；如果提交期间发生网络故障，数据库最终状态仍可能未知。

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

也可以在注册时同时配置常用数据库选项和 OpenTelemetry：

```csharp
builder.Services.AddDbContext<AppDbContext>(
    options => options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres")!)
        .UseConnectionTimeout(TimeSpan.FromSeconds(5))
        .UseCommandTimeout(TimeSpan.FromSeconds(30))
        .EnableConnectionPooling()
        .UseMinPoolSize(0)
        .UseMaxPoolSize(100),
    registration => registration.EnableOpenTelemetry = true);
```

连接与命令超时按秒向上取整，零表示不设超时。上述配置写入由本库创建的数据源的连接字符串；传入已有 `NpgsqlDataSource` 时，连接超时和连接池须在创建该数据源时配置。命令超时对普通查询、写入和 Raw SQL 的命令也单独设置；使用外部数据源的建表与迁移命令使用该数据源的默认命令超时。Bulk COPY 使用 `BulkInsertOptions.Timeout`。

通过 DI 创建的上下文自动使用容器中的 `ILoggerFactory`，也可用 `UseLoggerFactory(...)` 覆盖。普通查询和写入的执行与完成日志使用 `Debug`，失败日志使用 `Error`，分类为 `Perigon.PostgreSQL`；通过应用的日志配置选择输出目标与级别。参数值默认隐藏，只有调用 `EnableSensitiveLogging()` 才会记录。SQL 文本本身以及数据库异常可能包含敏感信息。建表、迁移和 COPY 不经过上述命令日志路径。

`AddDbContext` 会在容器中按生效的连接字符串复用单例 `NpgsqlDataSource`，并在容器释放时释放它。这样每个 scoped `DbContext` 不会重复创建和销毁连接池。多租户场景如果每个请求都会产生新的连接字符串，会让数据源缓存随连接字符串数量增长；此时应在应用层按租户管理有限数量的 `NpgsqlDataSource`，再通过 `UseNpgsql(dataSource)` 注入，而不是为每个请求拼接新的连接字符串。

直接使用 `new AppDbContext(connectionString)` 仍由上下文自行创建和释放数据源，适合短期工具、迁移和脚本。长时间运行的服务应注册并共享一个 `NpgsqlDataSource`，或使用上面的 DI 注册方式。

### DbContext 池

`AddDbContextPool` 可在不同请求之间复用上下文实例和该实例已构建的模型：

```csharp
builder.Services.AddDbContextPool<AppDbContext>(
    options => options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")!),
    poolSize: 128);

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    // 无自定义请求状态时，不需要覆盖 OnPoolReturn。
}
```

`DbContextOptions<TContext>` 位于 `Perigon.PostgreSQL.Options` 命名空间。池大小限制**保留的空闲实例数**，不是并发请求数；池中无空闲实例时会创建新实例，池满时归还的实例会被释放。每个请求仍使用自己的 scoped 上下文，不能并发使用同一个实例。也可传入第二个注册选项委托，使用 `registration.EnableOpenTelemetry = true`。

池化上下文不再强制实现 `IPooledDbContext`。`DbContext` 基类提供默认空实现的 `protected virtual OnPoolReturn()`；没有自定义请求状态时无需覆盖它。上下文确实持有租户、用户、缓存引用等可变状态时，应在每次租用后由应用显式赋值，并在归还池时清理：

```csharp
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options)
    : DbContext(options)
{
    public int? TenantId { get; set; }

    protected override void OnPoolReturn()
        => TenantId = null;
}
```

属性初始化器或构造函数中的默认值只在实例首次创建时执行，不能当作每次从池租用时的重置；仅将 `TenantId` 声明为可空或设置默认值，仍可能把上一租约的值带入下一请求。全局过滤器应引用上下文属性（例如 `row => row.TenantId == TenantId`），每次租用后先设置当前租户，再开始查询。若子类自行缓存 `DbSet` 字段，也必须在 `OnPoolReturn()` 中清空该字段。配置和 Fluent 模型在池生命周期内必须稳定；只能有一个公开构造函数，且只接收 `DbContextOptions<TContext>`，避免长期持有 scoped 服务；不能用池配置委托按请求切换连接字符串。

旧代码实现 `IPooledDbContext.ResetState()` 仍可继续使用，作为兼容性钩子会在归还池时调用；新代码优先覆盖 `OnPoolReturn()`。如果两者都实现，两个钩子都会执行，均不得在其中启动数据库操作或保留上一租约的状态。

池化上下文的全局查询过滤器不能捕获局部对象或局部变量，否则会在构建模型时直接报错。请求相关属性必须在每次租用后设置，并在归还时通过 `OnPoolReturn()`（或兼容的 `ResetState()`）清除。

同一租约内会缓存 `DbSet`；归还池时清除这些包装对象，新租约使用重新绑定的 `DbSet`，稳定模型继续复用。旧的 `DbSet`、查询及 Raw SQL 对象不能跨 scope 使用。上下文本身也不能保存到 scope 外：由于实例会被重新租用，旧的上下文引用无法与新租约区分。手动释放上下文后，它仍会等到所属 scope 结束才归还池；应在结束 scope 前等待所有数据库操作，并在所有 scope 结束后再释放根容器。

### OpenTelemetry 与 Aspire

核心包包含 OpenTelemetry 集成。可以使用上面的 `AddDbContext` 注册选项，或在应用启动时单独调用一次：

```csharp
builder.Services.AddPerigonPostgreSqlOpenTelemetry();
```

该方法订阅 Npgsql 的追踪源和指标源，适用于连接字符串创建的数据源以及通过 `UseNpgsql(dataSource)` 传入的数据源。它与 `AddDbContext` 分开注册，因此不需要时不会启用采集；也不会自行配置导出器。多次调用本库的注册入口只会注册一次；若已有 Aspire Npgsql 客户端集成并启用了追踪与指标，无需再次调用此方法。使用 Aspire 时，继续使用服务默认配置中的 OpenTelemetry 和 OTLP 导出；其他应用需自行配置 OpenTelemetry 导出器。SQL 文本和自定义遥测配置可能包含敏感信息，请按应用需要配置采集与访问权限。

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
