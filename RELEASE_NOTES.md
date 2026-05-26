# Release Notes

## 0.1.0-preview2

- Expanded real PostgreSQL integration coverage for joins, paging, null semantics, array operators, JSONB operators, raw SQL execution, transaction rollback, and atomic multi-step writes.
- Added unit coverage for raw SQL positional parameter handling and Roslyn-based source generator output.
- Fixed JSONB containment translation so `JsonbContains(...)` casts parameters to `jsonb` in generated SQL.
- Continued packaging and sample readiness work for preview distribution.

## 0.1.0-preview1

- Initial preview package with PostgreSQL-only LINQ translation, deterministic SQL generation, NativeAOT-oriented metadata/materializer generation, no tracking, raw SQL, bulk insert, updates, deletes, arrays, JSONB, grouping, and transaction support.