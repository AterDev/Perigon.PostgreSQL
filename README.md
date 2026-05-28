# Perigon.PostgreSQL

Perigon.PostgreSQL is a PostgreSQL-only data access library for .NET applications. It provides an EF Core-like `DbContext` / `DbSet<T>` programming model while keeping the runtime small, explicit, and NativeAOT-friendly.

The library is designed for services that want predictable PostgreSQL SQL, no change tracking, no lazy loading, and direct access to PostgreSQL features such as arrays, JSONB, `COPY`, `ON CONFLICT`, and `RETURNING`.

## Goals

- PostgreSQL-only behavior built on Npgsql.
- NativeAOT-aware metadata, materialization, and write accessors through source generation.
- Deterministic SQL with `$1`, `$2`, ... positional parameters.
- No change tracking and no implicit client-side LINQ fallback.
- Compile-time analyzer warnings for common unsupported or unsafe query shapes.
- Simple use from console apps, workers, and ASP.NET Core apps.

## Current Capabilities

- Entity mapping by convention or with `[Table]` / `[Column]` attributes.
- LINQ query translation for common `Where`, ordering, paging, projection, distinct, joins, grouping, aggregates, arrays, and JSONB extensions.
- Async query execution with `ToListAsync`, scalar projections, `CountAsync`, `AnyAsync`, `FirstOrDefaultAsync`, and `SingleOrDefaultAsync`.
- Insert, update, delete, bulk insert, insert returning, and upsert APIs.
- PostgreSQL-native array operations: `ANY`, `@>`, `&&`, `<@`, `cardinality`, and array aggregates.
- PostgreSQL JSONB operations: containment, key exists, text extraction, and JSONPath exists.
- Raw SQL query and command APIs using interpolated parameters.
- Split-query association loading through `IncludeManyAsync`.
- Source generator and analyzer are included in the main NuGet package.

Unsupported query shapes throw clear exceptions instead of falling back to client evaluation. Some examples are local method calls inside queries, `Expression.Invoke`, dynamic JSON POCO mapping, lazy loading, and arbitrary object graph JSON mapping without a source-generated path.

## Install

From NuGet:

```powershell
dotnet add package Perigon.PostgreSQL
```

For local package testing from this repository:

```powershell
.\scripts\pack.ps1
dotnet nuget add source .\artifacts\packages --name PerigonLocal
dotnet add package Perigon.PostgreSQL --version 1.0.0 --source PerigonLocal
```

To create a different package version:

```powershell
.\scripts\pack.ps1 -Version 1.0.1
```

When repeatedly testing local packages, use a new `-Version` value or clear the NuGet global package cache so the consuming project restores the latest nupkg.

The package includes:

- `lib/net10.0/Perigon.PostgreSQL.dll`
- `analyzers/dotnet/cs/Perigon.PostgreSQL.SourceGeneration.dll`
- `analyzers/dotnet/cs/Perigon.PostgreSQL.Analyzers.dll`

## Define a Model

```csharp
using Perigon.PostgreSQL.Attributes;

[Table("users")]
public sealed class User
{
	[Column("id", IsPrimaryKey = true, IsIdentity = true)]
	public int Id { get; set; }

	[Column("user_name")]
	public string UserName { get; set; } = "";

	[Column("age")]
	public int Age { get; set; }

	[Column("tags", IsArray = true)]
	public string[]? Tags { get; set; }

	[Column("profile_json", TypeName = "jsonb")]
	public string? ProfileJson { get; set; }
}
```

## Define a Context

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
		: base(options => options.UseNpgsql(connectionString))
	{
	}

	public DbSet<User> Users => Set<User>();
}
```

## Console App Example

```csharp
using Perigon.PostgreSQL;

var connectionString = "Host=localhost;Port=5432;Database=app;Username=postgres;Password=postgres";
await using var db = new AppDbContext(connectionString);

var activeUsers = await db.Users
	.Where(user => user.Age >= 18 && user.Tags!.Contains("postgres"))
	.OrderBy(user => user.UserName)
	.Take(20)
	.ToListAsync();

var inserted = await db.Users.InsertAsync(new User
{
	UserName = "alice",
	Age = 31,
	Tags = ["developer", "postgres"],
	ProfileJson = """{"level":3}"""
});
```

## ASP.NET Core Example

```csharp
using Perigon.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
	?? "Host=localhost;Port=5432;Database=app;Username=postgres;Password=postgres";

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

app.MapGet("/users", async (AppDbContext db, string? tag) =>
{
	IQueryable<User> query = db.Users;

	if (!string.IsNullOrWhiteSpace(tag))
	{
		query = query.Where(user => user.Tags!.Contains(tag));
	}

	return await query
		.OrderBy(user => user.UserName)
		.ToListAsync();
});

app.MapPost("/users", async (AppDbContext db, User user) =>
	Results.Created($"/users/{user.Id}", await db.Users.InsertAsync(user)));

app.Run();
```

`AddDbContext` registers the context as scoped by default. The factory form keeps construction explicit and NativeAOT-friendly.

## Query Examples

Arrays:

```csharp
var users = await db.Users
	.Where(user => user.Tags!.Contains("postgres"))
	.ToListAsync();
```

JSONB:

```csharp
var users = await db.Users
	.Where(user => user.ProfileJson.JsonbPathExists("$.level ? (@ > 2)"))
	.ToListAsync();
```

Update and delete require a filter unless you explicitly opt in to full-table mutations:

```csharp
await db.Users
	.Where(user => user.UserName == "alice")
	.ExecuteUpdateAsync(setters => setters.Set(user => user.Age, 32));

await db.Users
	.Where(user => user.UserName == "alice")
	.ExecuteDeleteAsync();
```

Raw SQL remains parameterized when using interpolated strings:

```csharp
var name = "alice";
var users = await db.SqlQuery<User>($"SELECT * FROM users WHERE user_name = {name}")
	.ToListAsync();
```

## Analyzer Warnings

The package automatically enables analyzers in consuming projects. Current warnings include:

- `PG001`: update/delete query has no visible `Where(...)` and no explicit full-table option.
- `PG002`: query expression contains clearly unsupported method calls, such as local methods, `Expression.Invoke`, `System.Math`, or culture-aware string overloads.
- `PG003`: JSONB property uses dynamic POCO mapping instead of `string`, `JsonDocument`, `JsonElement`, or a source-generated JSON path.

## Sample Project

The repository includes an ASP.NET Core minimal API sample:

```text
samples/Perigon.PostgreSQL.AspNetCoreSample
```

Run PostgreSQL for the sample:

```powershell
cd .\samples\Perigon.PostgreSQL.AspNetCoreSample
docker compose up -d
```

Run the API from the repository root:

```powershell
dotnet run --project .\samples\Perigon.PostgreSQL.AspNetCoreSample\Perigon.PostgreSQL.AspNetCoreSample.csproj --urls http://localhost:5088
```

Seed and query data:

```powershell
Invoke-RestMethod -Method Post http://localhost:5088/seed
Invoke-RestMethod "http://localhost:5088/users?status=active&tag=postgres&minAge=18"
Invoke-RestMethod "http://localhost:5088/users/with-blogs?status=active&publicOnly=true"
```

## Repository

Source code and sample projects are available at:

```text
https://github.com/AterDev/Perigon.PostgreSQL
```
