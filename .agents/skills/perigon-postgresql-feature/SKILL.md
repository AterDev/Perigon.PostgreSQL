---
name: perigon-postgresql-feature
description: "Use when adding or changing Perigon.PostgreSQL features, especially LINQ support, SQL translation, PostgreSQL execution, NativeAOT-safe materialization, DML, bulk insert, arrays, JSONB, include, group by, tests, or docs."
argument-hint: "Feature or bug to implement in Perigon.PostgreSQL"
---
# Perigon.PostgreSQL Feature Workflow

Use this skill for repeatable implementation work in this repository. Keep context lean: inspect nearby code/tests first, then load only the doc section that answers a specific uncertainty.

## 1. Choose Minimal Context

Do not read all docs at the start. Use this routing instead:

- `docs/04-LINQ支持矩阵.md`: support status, planned/unsupported boundary, matrix updates.
- `docs/02-详细技术文档.md`: architecture, AOT, SQL generation, source generation, execution, transactions.
- `docs/03-测试用例文档.md`: test scenarios and completion criteria.
- `docs/01-数据库设计文档.md`: product positioning and non-goals.

Prefer search results and targeted line ranges over full document reads.

Classify the requested work as one of: query translation, projection/materialization, aggregate/grouping, join/include, DML, bulk write, raw SQL, metadata/source generation, analyzer, docs, or sample work.

## 2. Locate Existing Patterns

Search for nearby implementation and tests before editing. Prefer extending current patterns:

- `src/Perigon.PostgreSQL/Expressions` for expression translation.
- `src/Perigon.PostgreSQL/Query` for query model and queryable behavior.
- `src/Perigon.PostgreSQL/Sql` for SQL building and parameter allocation.
- `src/Perigon.PostgreSQL/Execution` for command execution and materialization.
- `src/Perigon.PostgreSQL/Bulk`, `Update`, and `RawSql` for write paths.
- `tests/Perigon.PostgreSQL.Tests` for SQL, metadata, and diagnostics tests.
- `tests/Perigon.PostgreSQL.IntegrationTests` for real PostgreSQL behavior.

## 3. Define SQL and Semantics

Before implementing, write down the intended behavior:

- SQL text shape and aliases.
- Stable `$n` parameter order.
- Parameter values and database types when relevant.
- Null behavior and empty collection behavior.
- Whether PostgreSQL execution is supported or only SQL preview is supported.
- Unsupported variants that should throw `UnsupportedQueryExpressionException`.

Use PostgreSQL-native forms such as `= ANY($1)`, `@>`, `&&`, `<@`, `cardinality`, `jsonb` operators, `RETURNING`, `COPY FROM STDIN`, and `ON CONFLICT` when they match the feature.

## 4. Add Tests at the Right Level

For a new supported LINQ/API item, add only the tests relevant to the change:

1. SQL translation or snapshot-style unit test.
2. Parameter order/value/type assertion.
3. Unsupported-shape diagnostic test.
4. Real PostgreSQL integration test.
5. NativeAOT smoke validation when the change affects source generation, materialization, trimming, or runtime activation.

For DML, also test no-where protection, explicit full-table opt-in, affected rows, null assignment, and rollback when relevant.

For bulk operations, also test empty input, one row, normal batch, large batch, and batching/parameter limits.

## 5. Implement Conservatively

- Prefer adding a narrow translation branch over a broad expression evaluator.
- Keep SQL construction centralized in existing builders.
- Never concatenate user values into SQL.
- Keep identifiers on the repository's quoting path.
- Throw clear unsupported exceptions instead of approximating behavior.
- Preserve AOT compatibility: no dynamic proxy, runtime assembly scan, `Reflection.Emit`, or `Expression.Compile()` execution path.

## 6. Update Documentation

After tests and implementation agree, update `docs/04-LINQ支持矩阵.md` if the supported surface changed.

Use these statuses consistently:

- `已实现` for fully implemented and tested behavior.
- `已实现基础版` for implemented core behavior with documented limitations.
- `已实现 SQL 预览` for SQL generation without full execution support.
- `计划支持` for intended but incomplete behavior.
- `暂不支持` for intentionally unsupported behavior.

## 7. Validate

Run the narrowest useful command first, then broaden only when the changed area warrants it:

```powershell
dotnet build .\Perigon.PostgreSQL.slnx
dotnet test .\tests\Perigon.PostgreSQL.Tests\Perigon.PostgreSQL.Tests.csproj
dotnet test .\tests\Perigon.PostgreSQL.IntegrationTests\Perigon.PostgreSQL.IntegrationTests.csproj
dotnet publish .\tests\Perigon.PostgreSQL.NativeAotSmoke\Perigon.PostgreSQL.NativeAotSmoke.csproj -c Release -r win-x64 /p:PublishAot=true
```

If PostgreSQL or Docker is unavailable, state which integration validation could not run and keep the unit/build validation complete.

## Final Response Checklist

Report:

- What changed.
- Why the change matches the docs and LINQ matrix.
- Which tests or commands ran.
- Any skipped validation and why.