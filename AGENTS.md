# Perigon.PostgreSQL AI Coding Instructions

Perigon.PostgreSQL is a PostgreSQL-only .NET data access library with an EF Core-like DbContext/DbSet feel, NativeAOT compatibility, no change tracking, deterministic SQL, and PostgreSQL-native features.

## Context Budget

- Do not read all docs by default. Start from the user's request, nearby code/tests, and `docs/04-LINQ支持矩阵.md` only when the supported LINQ/API boundary is unclear.
- Read `docs/04-LINQ支持矩阵.md` only to confirm support status or update the matrix.
- Read `docs/01-数据库设计文档.md` only for product scope or non-goal questions.
- Read `docs/02-详细技术文档.md` only for architecture, AOT, SQL generation, source generation, execution, or transaction changes.
- Read `docs/03-测试用例文档.md` only when designing or expanding test coverage.
- Prefer targeted line ranges or searches over whole-file reads.

## Constraints

- Do not introduce cross-database provider abstractions.
- Do not add change tracking, lazy loading, dynamic proxies, or EF Core compatibility beyond documented scope.
- Do not use client-side fallback for unsupported LINQ translation.
- Do not add AOT-hostile execution paths such as runtime assembly scanning, `Reflection.Emit`, or `Expression.Compile()` for query execution.
- Do not concatenate user values into SQL.
- Do not mark a LINQ/API feature as supported in docs until SQL snapshot/unit tests and real PostgreSQL integration tests exist, unless the user explicitly asks for documentation-only work.

## Workflow

1. Classify the task as query translation, execution/materialization, DML, bulk, metadata, source generation, analyzer, tests, docs, or sample work.
2. Locate the current implementation and nearest tests before editing.
3. Read only the minimal doc section needed to resolve unclear support, architecture, or test expectations.
4. Define the expected SQL shape, parameter order, null semantics, and unsupported-expression behavior.
5. Add or update targeted tests first when feasible.
6. Implement the smallest production change that satisfies the tests and existing docs.
7. Update `docs/04-LINQ支持矩阵.md` when the supported surface changes.
8. Run the narrowest relevant test command, then broader build/test commands when the blast radius is larger.


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