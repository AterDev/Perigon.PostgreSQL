# Perigon.PostgreSQL

Perigon.PostgreSQL 是一个面向 PostgreSQL 的 .NET 数据访问库，提供类似 EF Core 的 `DbContext` / `DbSet<T>` 使用方式。

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
dotnet tool install --global dotnet-perigon
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
