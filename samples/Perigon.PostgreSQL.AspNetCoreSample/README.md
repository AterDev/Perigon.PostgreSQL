# Perigon.PostgreSQL ASP.NET Core Sample

This sample demonstrates using `Perigon.PostgreSQL` from an ASP.NET Core minimal API.

It covers:

- Conditional entity queries.
- Array filtering.
- Single-row insert.
- Binary COPY bulk insert.
- Batched `INSERT ... VALUES` bulk insert fallback.
- Conditional update and delete.
- Computed update with `SetExpression`.
- Upsert with `ON CONFLICT`.
- Association queries through related tables.
- Filtered split-query association loading.
- Executed LINQ join projection reports.
- Executed LINQ multi-key group-by statistics.
- Distinct scalar projections.
- PostgreSQL JSONPath filters.
- Count-distinct aggregate projections.
- Report queries with joins.
- Statistics queries with `GROUP BY`.
- Daily, monthly, and quarterly created-at statistics endpoints.
- A `DateTimeOffset` range-statistics endpoint with explicit `from` / `to` query input.
- SQL preview for LINQ join and group-by translations.
- SQL preview for LINQ left join through `GroupJoin` + `SelectMany` + `DefaultIfEmpty`.

## Start PostgreSQL

From this sample directory:

```powershell
docker compose up -d
```

The default connection string is:

```text
Host=localhost;Port=55432;Database=perigon_sample;Username=postgres;Password=postgres
```

## Run the API

From the repository root:

```powershell
dotnet run --project .\samples\Perigon.PostgreSQL.AspNetCoreSample\Perigon.PostgreSQL.AspNetCoreSample.csproj --urls http://localhost:5088
```

Examples:

```powershell
Invoke-RestMethod -Method Post http://localhost:5088/seed
Invoke-RestMethod "http://localhost:5088/users?status=active&tag=postgres&minAge=18"
Invoke-RestMethod "http://localhost:5088/users/summaries?status=active"
Invoke-RestMethod "http://localhost:5088/users/names?status=active"
Invoke-RestMethod "http://localhost:5088/users/statuses"
Invoke-RestMethod "http://localhost:5088/users/jsonpath?path=$.level%20?%20(@%20%3E%202)"
Invoke-RestMethod "http://localhost:5088/users/with-blogs?status=active&publicOnly=true"
Invoke-RestMethod -Method Patch "http://localhost:5088/users/1/age/increment"
Invoke-RestMethod "http://localhost:5088/reports/user-blog-links?publicOnly=true"
Invoke-RestMethod "http://localhost:5088/reports/user-blogs"
Invoke-RestMethod "http://localhost:5088/stats/users-by-status"
Invoke-RestMethod "http://localhost:5088/stats/distinct-active-by-status"
Invoke-RestMethod "http://localhost:5088/stats/users-created/daily?from=2026-01-01T00:00:00Z"
Invoke-RestMethod "http://localhost:5088/stats/users-created/monthly?from=2026-01-01T00:00:00Z"
Invoke-RestMethod "http://localhost:5088/stats/users-created/quarterly?from=2026-01-01T00:00:00Z"
Invoke-RestMethod "http://localhost:5088/stats/users-created/range-offset?status=active&from=2026-01-01T08:00:00%2B08:00&to=2026-02-01T08:00:00%2B08:00"
Invoke-RestMethod "http://localhost:5088/sql-preview/join"
Invoke-RestMethod "http://localhost:5088/sql-preview/left-join"
Invoke-RestMethod "http://localhost:5088/sql-preview/group-by"
```

For time-range and bucket endpoints, prefer UTC ISO 8601 input such as `2026-01-01T00:00:00Z`. The `/stats/users-created/range-offset` endpoint also accepts offset-aware input such as `2026-01-01T08:00:00+08:00` and normalizes it to UTC before querying PostgreSQL.

Stop PostgreSQL:

```powershell
docker compose down
```
