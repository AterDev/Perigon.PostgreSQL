# Perigon.PostgreSQL AI Coding Instructions

Perigon.PostgreSQL is a PostgreSQL-only .NET data access library with an EF Core-like DbContext/DbSet feel, NativeAOT compatibility, no change tracking, deterministic SQL, and PostgreSQL-native features.

## Context Budget

- Do not read all docs by default. Start from the user's request, nearby code/tests, and `docs/04-LINQ支持矩阵.md` only when the supported LINQ/API boundary is unclear.
- Read `docs/01-数据库设计文档.md` only for product scope or non-goal questions.
- Read `docs/02-详细技术文档.md` only for architecture, AOT, SQL generation, source generation, execution, or transaction changes.
- Read `docs/03-测试用例文档.md` only when designing or expanding test coverage.
- Prefer targeted line ranges or searches over whole-file reads.

## Non-Negotiables

- PostgreSQL only: use `$1` positional parameters, quoted identifiers, Npgsql, arrays, JSONB, COPY, RETURNING, and ON CONFLICT when relevant.
- NativeAOT safe: avoid runtime assembly scanning, `Reflection.Emit`, dynamic proxies, lazy loading, `Expression.Compile()` execution paths, and hot-path reflection.
- No tracking and no client fallback: unsupported LINQ must throw `UnsupportedQueryExpressionException` instead of silently evaluating on the client.
- SQL safety: parameterize values and route identifiers through existing quoting paths.
- New supported LINQ/API behavior needs SQL/unit coverage, real PostgreSQL integration coverage when executable, and a matrix update.

## Useful Commands

```powershell
dotnet build .\Perigon.PostgreSQL.slnx
dotnet test .\tests\Perigon.PostgreSQL.Tests\Perigon.PostgreSQL.Tests.csproj
dotnet test .\tests\Perigon.PostgreSQL.IntegrationTests\Perigon.PostgreSQL.IntegrationTests.csproj
dotnet publish .\tests\Perigon.PostgreSQL.NativeAotSmoke\Perigon.PostgreSQL.NativeAotSmoke.csproj -c Release -r win-x64 /p:PublishAot=true
```

Integration tests require PostgreSQL/Docker. If unavailable, say so and run the unit/build validation that does not require it.