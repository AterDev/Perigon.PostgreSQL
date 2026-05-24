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
Invoke-RestMethod "http://localhost:5088/sql-preview/join"
Invoke-RestMethod "http://localhost:5088/sql-preview/left-join"
Invoke-RestMethod "http://localhost:5088/sql-preview/group-by"
```

Stop PostgreSQL:

```powershell
docker compose down
```
