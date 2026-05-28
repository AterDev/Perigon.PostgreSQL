# Release Notes

## 0.1.2

- Renamed `PostgresDbContext` to `DbContext` across the library and aligned related APIs, query infrastructure, and sample code with the new surface.
- Added ASP.NET Core registration support through `PostgresServiceCollectionExtensions`, expanded README/sample coverage, and improved packaging assets and release readiness.
- Added PostgreSQL schema metadata and DDL support, including views, indexes, foreign keys, referential actions, catalog reading, `EnsureCreated`, and scaffold code generation.
- Added richer model metadata and Fluent API configuration support for relationships, indexes, comments, precision, and Unicode settings, with improved PostgreSQL type mapping for numeric and timestamp columns.
- Expanded integration, analyzer, source-generation, and SQL test coverage for transactions, raw SQL, JSONB, arrays, schema creation, fluent metadata, and generated model output.

## 0.1.0-preview2

- Expanded real PostgreSQL integration coverage for joins, paging, null semantics, array operators, JSONB operators, raw SQL execution, transaction rollback, and atomic multi-step writes.
- Added unit coverage for raw SQL positional parameter handling and Roslyn-based source generator output.
- Fixed JSONB containment translation so `JsonbContains(...)` casts parameters to `jsonb` in generated SQL.
- Continued packaging and sample readiness work for preview distribution.

## 0.1.0-preview1

- Initial preview package with PostgreSQL-only LINQ translation, deterministic SQL generation, NativeAOT-oriented metadata/materializer generation, no tracking, raw SQL, bulk insert, updates, deletes, arrays, JSONB, grouping, and transaction support.