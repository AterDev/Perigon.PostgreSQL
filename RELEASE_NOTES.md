# Release Notes

## 0.2.1

- Added named global query filters for structured queries and updates/deletes, including filtered navigation loading and explicit per-query filter bypass.
- Added opt-in client-side UTC timestamp generation and automatic GUID v7 generation for empty, writable single-column GUID primary keys, including inherited properties and bulk writes.
- Added transaction isolation options and clarified commit cancellation and unknown-outcome behavior. Multiple operations remain explicitly transaction-scoped without change tracking.
- Aligned the migration and scaffolding tool package with the runtime release. Raw SQL bypasses query filters and automatic client-side values; tenant values on inserts remain the application's responsibility.

## 0.2.0

- Added source-generated, NativeAOT-compatible single-level reference and collection navigation `Include`, including filtered collection loading and per-parent pagination, and explicit-key split-query loading for many-to-many relationships.
- Expanded Fluent relationship mapping and source-generated foreign key metadata, including principal-side one-to-many and explicit join-entity many-to-many configuration.
- Added post-projection filtering, sorting and paging, correlated `Any`, `All`, `Count`, `LongCount`, `Sum`, `Min`, `Max` and `Average` support for the documented query shapes, and correct handling of empty non-nullable aggregate projections.
- Added PostgreSQL integration and NativeAOT smoke coverage; unsupported navigation, aggregate and LINQ shapes continue to fail explicitly. See the LINQ support matrix for current limitations.

## 0.1.8

- Fixed recursive dependency resolution in the migration CLI that could repeatedly enumerate shared framework directories and overflow the stack when an assembly could not be resolved.

## 0.1.7

- Fixed clean-build packaging of `Perigon.PostgreSQL` so `Perigon.PostgreSQL.SourceGeneration.dll` and `Perigon.PostgreSQL.Analyzers.dll` are included under `analyzers/dotnet/cs`. This ensures generated entity model registration is available to package consumers, including migration and schema-creation flows.

## 0.1.6

- Added incremental PostgreSQL schema migrations with deterministic model snapshots and diffs, generated `Up`/`Down` migration source, SQL scripts, database update and baseline commands, migration history tracking, transactional application with advisory locking, and NativeAOT-safe static migration registration. Existing `EnsureCreated` databases can be validated and baselined before the migration system takes over.

## 0.1.5

- Made query translation fail explicitly for unsupported LINQ shapes, added exact string-method overload handling, escaped `LIKE` patterns with an explicit `ESCAPE` clause, and corrected per-parent `IncludeMany` paging and identity-key upsert validation.
- Hardened bulk writes and transactions with effective timeout/cancellation handling, streaming COPY input, atomic multi-batch operations, deterministic rollback cleanup, single-operation `DbContext` concurrency guards, and disposed-state checks.
- Added logger-factory integration and safer diagnostics, expanded source-generated materialization for public `set`/`init` properties and raw SQL DTOs, and improved NativeAOT/source-generation coverage.
- Updated the local packaging script to produce aligned `Perigon.PostgreSQL` and `Perigon.PostgreSQL.Tools` 0.1.5 packages.

## 0.1.4

- Renamed the reverse-engineering command to `dotnet perigon database scaffold`, added default `--context`, `--namespace`, and `--output` behavior, and standardized scaffold output under `AppDbContext/` plus `Entity/`.
- Expanded runtime metadata and `EnsureCreated` support for composite primary keys, composite foreign keys, column default SQL, generated column SQL, richer PostgreSQL index metadata, and composite-key `RETURNING` behavior.
- Expanded PostgreSQL catalog reading and scaffold code generation for composite keys/foreign keys, default SQL, generated expressions, advanced indexes, and view-aligned model output.
- Refreshed README and development docs to the new command surface and current support matrix, and validated unit tests, integration tests, and the NativeAOT smoke publish path on Windows.

## 0.1.3

- Added configurable command timeout, connection timeout, and connection pooling options while preserving Npgsql defaults when the options are not explicitly set.
- Expanded grouped date statistics support with daily, monthly, quarterly, and `DateTimeOffset` bucket projections, including UTC `make_timestamptz(...)` translation for grouped `DateTimeOffset` output.
- Added `DateTime` and `DateTimeOffset` range-filter coverage for half-open intervals, nullable timestamp predicates, equivalent instants across different offsets, and stable PostgreSQL behavior after `SET LOCAL TIME ZONE ...`.
- Normalized `DateTimeOffset` command parameters to UTC before binding to PostgreSQL `timestamp with time zone` values and materialized grouped `DateTimeOffset` results back as UTC offsets.
- Expanded the ASP.NET Core sample with direct statistics endpoints for daily, monthly, quarterly, and explicit `DateTimeOffset from/to` range reports, plus updated README/sample docs for timeout, pooling, and temporal query guidance.

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
