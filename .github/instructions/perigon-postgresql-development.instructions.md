---
description: "Use when editing Perigon.PostgreSQL C# source. Concise rules for NativeAOT, SQL generation, LINQ translation, and tests."
applyTo: "src/**/*.cs"
---
# Perigon.PostgreSQL Source Rules

- This is PostgreSQL-only, NativeAOT-oriented, and no-tracking. Do not add cross-database abstractions, change tracking, lazy loading, dynamic proxies, or automatic client fallback.
- Keep SQL structured and deterministic: stable `$n` parameters, quoted identifiers through existing helpers, values always parameterized.
- Unsupported LINQ/expression shapes must throw `UnsupportedQueryExpressionException` with useful context.
- Avoid AOT-hostile paths: runtime assembly scanning, `Reflection.Emit`, `Expression.Compile()` for execution, and hot-path reflection.
- For new supported behavior, add targeted unit/SQL tests and integration tests when executable, then update `docs/04-LINQ支持矩阵.md`.
- Do not load full docs automatically. Read only the relevant doc section when the nearby code/tests do not answer the question.