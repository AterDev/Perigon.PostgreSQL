# LINQ 支持矩阵

日期：2026-05-29

本文档定义当前实现支持的 LINQ/表达式范围，以及生产版本计划支持和明确不支持的边界。任何新增支持项都必须先补 SQL 快照测试，再补真实 PostgreSQL 集成测试。

状态说明：

- `已实现`：当前代码已有 SQL 翻译和单元测试。
- `计划支持`：生产版本目标能力，尚未实现或尚未完整测试。
- `暂不支持`：不作为当前库的 LINQ 翻译目标，用户应使用原生 SQL 或专用 API。

## 1. 查询入口

| LINQ/API | 状态 | SQL 形态 | 备注 |
| --- | --- | --- | --- |
| `DbSet<T>` | 已实现 | `FROM "table"` | 支持默认映射和 Table/Column 特性 |
| `Where` | 已实现 | `WHERE ...` | 支持多次链式组合 |
| `OrderBy` | 已实现 | `ORDER BY ... ASC` | 仅支持实体成员 |
| `OrderByDescending` | 已实现 | `ORDER BY ... DESC` | 仅支持实体成员 |
| `ThenBy` | 已实现 | 多列排序 | 仅支持实体成员 |
| `ThenByDescending` | 已实现 | 多列排序 | 仅支持实体成员 |
| `Skip` | 已实现 | `OFFSET $n` | 支持常量和捕获变量 |
| `Take` | 已实现 | `LIMIT $n` | 支持常量和捕获变量 |
| `HasQueryFilter` | 已实现基础版 | 自动追加 `WHERE` 条件 | 在 `OnModelCreating` 中按名称注册；表达式服务端翻译，不做客户端过滤 |
| `SetWithoutFilters<T>(...)` | 已实现基础版 | 省略指定过滤条件 | 具名跳过；空列表和未知名称抛异常 |
| `SetIgnoringAllFilters<T>()` | 已实现基础版 | 不追加全局过滤条件 | 显式跳过该实体的全部已注册过滤器 |
| `Select` 实体投影 | 已实现基础版 | `SELECT columns` | 默认选择实体全部列 |
| `Select` 标量投影 | 已实现基础版 | `SELECT column AS alias` | 通过 `ToScalarListAsync` 执行 |
| `Select` DTO/匿名类型 | 已实现基础版 | `SELECT col1 AS ..., col2 AS ...` | DTO 投影可执行，匿名类型限 SQL 预览；DTO 标量字段支持关联 `LongCount` / `Sum` 子查询 |
| 普通 `Select` 后的筛选、排序和分页 | 已实现基础版 | 投影子查询与外层 `WHERE/ORDER BY/LIMIT/OFFSET` | 支持标量及 DTO；分页后筛选/排序等复杂顺序明确拒绝 |
| `Distinct` | 已实现基础版 | `SELECT DISTINCT ...` | 支持实体/标量/DTO 投影后的 distinct |
| `Join` | 已实现基础版 | `INNER JOIN` | 支持等值连接及 DTO 执行；外表先 `Skip/Take` 时使用子查询，分页后筛选等复杂顺序暂不支持 |
| `GroupJoin` + `SelectMany` + `DefaultIfEmpty` | 已实现基础版 | `LEFT JOIN` | 标准左连接形态，inner filter 放入 ON；外表分页使用子查询 |
| `SelectMany` | 计划支持 | `JOIN` / `LATERAL` | 复杂形态后置 |

## 2. 执行操作

| API | 状态 | SQL 形态 | 备注 |
| --- | --- | --- | --- |
| `ToQuerySql` | 已实现 | 生成查询 SQL | 测试和诊断 API |
| `ToListAsync` | 已实现基础版 | 执行 SELECT | 优先使用源生成 materializer；未注册类型回退反射路径 |
| `FirstOrDefaultAsync` | 已实现基础版 | `LIMIT 1` | 通过 `Take(1)` 实现 |
| `SingleOrDefaultAsync` | 已实现基础版 | `LIMIT 2` | 客户端检测多行 |
| `ToCountSql` | 已实现 | `SELECT count(*)` | 测试和诊断 API |
| `ToAnySql` | 已实现 | `SELECT 1 ... LIMIT 1` | 测试和诊断 API |
| `CountAsync` | 已实现基础版 | `SELECT count(*)` | Docker PostgreSQL 集成测试覆盖 |
| `AnyAsync` | 已实现基础版 | `SELECT 1 ... LIMIT 1` | Docker PostgreSQL 集成测试覆盖 |
| `LongCountAsync` | 已实现基础版 | `SELECT count(*)` | 返回 long |
| `Queryable.Any` 子查询 | 已实现基础版 | `EXISTS (SELECT 1 ...)` | 支持关联 `Where` 等值条件；子查询不支持排序/分页 |
| `Queryable.Count` 子查询 | 已实现基础版 | `(SELECT count(*)::integer ...)` | 支持筛选条件及 DTO 投影中使用关联计数；溢出由 PostgreSQL 抛出异常；子查询不支持排序/分页 |
| `Queryable.LongCount` 子查询 | 已实现基础版 | `(SELECT count(*) ...)` | 支持筛选条件及 DTO 投影中使用关联计数；子查询不支持排序/分页 |
| `Queryable.Sum` 子查询 | 已实现基础版 | `(SELECT COALESCE(sum(...), 0) ...)` | 支持标量选择器及 DTO 投影；空集合按 LINQ Sum 返回 0；int/long 结果显式转换，溢出由 PostgreSQL 抛错；子查询不支持排序/分页 |
| `Queryable.Min/Max/Average` 子查询 | 已实现基础版 | `(SELECT min/max/avg(...) ...)` | WHERE 中仅支持 nullable 结果；直接 DTO 投影可支持 non-nullable 结果，空集合物化时抛 `InvalidOperationException`；Average(double) 显式转换为 `double precision`；bool/Guid Min/Max 明确拒绝；子查询不支持排序/分页 |
| `Queryable.All` 子查询 | 已实现基础版 | `NOT EXISTS (SELECT 1 ... WHERE predicate IS NOT TRUE)` | 支持关联谓词和捕获查询；子查询不支持排序/分页，保留 null 违反全称谓词语义 |

## 3. Where 表达式

| 表达式 | 状态 | SQL 形态 |
| --- | --- | --- |
| `u.Id == id` | 已实现 | `u.id = $1` |
| `u.Id != id` | 已实现 | `u.id <> $1` |
| `u.Age > age` | 已实现 | `u.age > $1` |
| `u.Age >= age` | 已实现 | `u.age >= $1` |
| `u.Age < age` | 已实现 | `u.age < $1` |
| `u.Age <= age` | 已实现 | `u.age <= $1` |
| `a && b` | 已实现 | `(a AND b)` |
| `a || b` | 已实现 | `(a OR b)` |
| `!u.IsActive` | 已实现 | `NOT (u.is_active)` |
| `u.IsActive` | 已实现 | `u.is_active` |
| `u.Status == null` | 已实现 | `IS NULL` |
| `u.Status != null` | 已实现 | `IS NOT NULL` |
| 捕获变量为 null | 已实现 | `IS NULL` / `IS NOT NULL` |
| `Nullable<T>.HasValue` | 已实现 | `IS NOT NULL` |
| `Nullable<T>.Value` | 已实现 | 列表达式 |
| 日期范围 | 已实现 | 两个参数比较 |
| `DateTimeOffset` 时间范围 | 已实现基础版 | `timestamp with time zone` 参数比较；参数绑定时归一化为 UTC instant |
| `DateTimeOffset?` null / `HasValue` / `Value` | 已实现基础版 | `IS NULL` / `IS NOT NULL` / 列比较 |
| 半开时间区间 `>= start && < end` | 已实现 | 推荐时间范围写法；SQL 和真实 PostgreSQL 集成测试覆盖 |
| 本地方法调用 | 暂不支持 | 抛出 `UnsupportedQueryExpressionException` |
| `Expression.Invoke` | 暂不支持 | 抛异常 |
| 客户端执行回退 | 暂不支持 | 禁止 |

## 4. 字符串方法

| C# | 状态 | SQL |
| --- | --- | --- |
| `u.Name.Contains(value)` | 已实现 | `LIKE $1`，参数为 `%value%` |
| `u.Name.StartsWith(value)` | 已实现 | `LIKE $1`，参数为 `value%` |
| `u.Name.EndsWith(value)` | 已实现 | `LIKE $1`，参数为 `%value` |
| `u.Name.ToLower()` | 已实现 | `lower(column)` |
| `u.Name.ToUpper()` | 已实现 | `upper(column)` |
| `string.IsNullOrEmpty(u.Name)` | 已实现 | `column IS NULL OR column = ''` |
| `string.IsNullOrWhiteSpace` | 已实现 | `column IS NULL OR btrim(column) = ''` |
| `Trim/TrimStart/TrimEnd` | 已实现 | `btrim/ltrim/rtrim` |
| `Substring` | 已实现 | `substring(column from start for length)`，C# 0 基索引转 PostgreSQL 1 基索引 |
| `Length` | 已实现 | `length(column)` |
| culture-aware overloads | 暂不支持 | PostgreSQL collation 需显式 API |

## 5. 集合和数组

| C# | 状态 | SQL |
| --- | --- | --- |
| `ids.Contains(u.Id)` | 已实现 | `u.id = ANY($1)` |
| `u.Tags.Contains("x")` | 已实现 | `u.tags @> ARRAY[$1]` |
| `u.Tags.ContainsAll(tags)` | 已实现 | `u.tags @> $1` |
| `u.Tags.Overlaps(tags)` | 已实现 | `u.tags && $1` |
| `u.Tags.IsContainedBy(tags)` | 已实现 | `u.tags <@ $1` |
| `u.Tags.Length` | 已实现 | `cardinality(u.tags)` |
| 空数组参数 | 已实现 | 仍作为参数绑定 |
| `u.Tags.Any()` | 已实现 | `cardinality(u.tags) > 0` |
| `u.Tags.Any(t => t == value)` | 已实现 | `$1 = ANY(u.tags)` |
| `u.Tags.All(t => t == value)` | 已实现基础版 | `NOT EXISTS (SELECT 1 FROM unnest(u.tags) ... IS DISTINCT FROM $1)` |
| 多维数组 | 暂不支持 | 后置 |

## 6. JSONB

| C# 扩展 | 状态 | SQL |
| --- | --- | --- |
| `u.ProfileJson.JsonbContains(json)` | 已实现 | `profile_json @> $1` |
| `u.ProfileJson.JsonbHasKey(key)` | 已实现 | `profile_json ? $1` |
| `u.ProfileJson.JsonbText(key)` | 已实现 | `profile_json ->> $1` |
| `u.ProfileJson.JsonbPathExists(path)` | 已实现基础版 | `profile_json @? ($1)::jsonpath` |
| 强类型 JSON source-gen 映射 | 计划支持 | 读写层能力 |
| 动态 JSON POCO | 暂不支持 | AOT 边界 |

## 7. DML 相关 LINQ

| API | 状态 | SQL |
| --- | --- | --- |
| `InsertAsync(entity)` | 已实现基础版 | `INSERT ... RETURNING` |
| `InsertAsync(entity, returning: false)` | 已实现基础版 | `INSERT` |
| `ToInsertSql` | 已实现 | 诊断 SQL |
| `Where(...).ExecuteDeleteAsync()` | 已实现基础版 | `DELETE ... WHERE` |
| `ToDeleteSql` | 已实现 | 诊断 SQL |
| 无 WHERE delete | 已实现保护 | 默认抛异常 |
| 显式全表 delete | 已实现 | 需要 `AllowFullTableDelete` |
| `Where(...).ExecuteUpdateAsync(setters)` | 已实现基础版 | `UPDATE ... SET ... WHERE` |
| `ToUpdateSql` | 已实现 | 诊断 SQL |
| 多列 `Set` | 已实现 | 多列 `SET` |
| 更新为 null | 已实现 | null 参数 |
| 表达式更新 `Age = Age + 1` | 已实现基础版 | `SetExpression(..., u => u.Age + 1)` |
| `BulkInsertAsync` | 已实现基础版 | Binary `COPY FROM STDIN` 或批量 `INSERT ... VALUES`；COPY 流式写入，InsertValues 多批次默认原子并支持超时 |
| `InsertManyReturningAsync` | 已实现基础版 | 多值 INSERT RETURNING；多批次默认原子 |
| `UpsertManyAsync` | 已实现基础版 | ON CONFLICT，支持 identity conflict key |
| `MergeAsync` | 计划支持 | PostgreSQL 15+ MERGE |
| 单列 Guid 主键为空时生成版本 7 UUID | 已实现基础版 | 客户端 `Guid.CreateVersion7()`；非默认值保留 |
| `[AutoTimestamp]` / `HasAutoTimestamp` | 已实现基础版 | 客户端 UTC 时间；插入默认值填充，更新自动更新时间 |
| 原生 `SqlCommand` / `SqlQuery` 的自动值 | 不适用 | 原生 SQL 绕过客户端值生成 |

## 8. Include 和导航

| API | 状态 | 计划 SQL |
| --- | --- | --- |
| `Include(reference)` | 已实现基础版 | 单层多对一引用，单列外键，源生成 loader，split query |
| `Include(collection)` | 已实现基础版 | 单层一对多 `List<T>` 集合，单列外键，源生成 loader，split query |
| `IncludeManyAsync(parentKey, childForeignKey, resultSelector)` | 已实现基础版 | split query，子查询使用 `ANY($1)` |
| `IncludeManyToManyAsync` | 已实现基础版 | 通过显式中间实体分三次查询加载；单列键，支持服务器端子项过滤，不自动填充导航属性，无排序保证 |
| `IncludeAsync`（一对多/显式多对多重载） | 已实现基础版 | 与 `IncludeManyAsync` / `IncludeManyToManyAsync` 相同；必须显式提供键 selector，不是 EF 导航 Include |
| `HasMany().WithMany().UsingEntity<TJoin>(...)` | 已实现基础版 | 显式连接实体生成两条 `FOREIGN KEY`；AOT 安全；连接实体可配置复合主键 |
| 多对多隐式写入 | 不支持 | 必须直接操作显式中间实体 `DbSet` |
| filtered include | 已实现基础版 | 集合导航支持 `Where/OrderBy/ThenBy/Skip/Take`；分页按父级执行并要求排序，追加子实体主键稳定排序 |
| `ThenInclude` | 计划支持 | 多级 split query |
| `AsSplitQuery` | 计划支持 | 多查询 |
| `AsSingleQuery` | 计划支持 | LEFT JOIN |
| lazy loading | 暂不支持 | 无追踪/AOT 边界 |

导航 `Include` 要求源生成器能够通过命名约定或 `[ForeignKey]` 在同一 `DbContext` 中识别关系；仅在 `OnModelCreating` 中配置的非约定关系不会生成加载器。导航属性需有公开 setter、标记 `[NotMapped]`，目标实体需要公开无参构造函数。生成的关系必须与最终 Fluent 配置匹配；不支持的导航会在调用时抛异常。集合筛选支持先 `Where`、排序，再 `Skip/Take`；分页要求 `OrderBy` 和子实体主键。同一导航重复 Include、投影后保留导航加载、多对多导航和 `ThenInclude` 暂不支持。无跟踪或自动关系修复；仅使用 split query。

```csharp
var users = await db.Users
    .Include(user => user.Posts)
    .Where(user => user.IsActive)
    .ToListAsync();

var posts = await db.Posts
    .Include(post => post.User)
    .ToListAsync();

var filtered = await db.Users
    .Include(user => user.Posts.Where(post => post.IsPublished)
        .OrderByDescending(post => post.CreatedAt).Take(5))
    .ToListAsync();
```

## 9. GroupBy 和聚合

| LINQ | 状态 | SQL |
| --- | --- | --- |
| `GroupBy(...).Select(g => new { g.Key, Count = g.Count() })` | 已实现 SQL 预览 | `GROUP BY`, `count(*)` |
| `Sum/Min/Max/Average` | 已实现 SQL 预览 | 聚合函数 |
| 多 key group | 已实现基础版 | 多列 `GROUP BY`，支持 DTO/匿名类型投影 |
| `GroupBy(x => x.OrderTime.Date)` | 已实现基础版 | `date_trunc('day', column)` |
| `GroupBy(x => new { x.OrderTime.Year, x.OrderTime.Month })` | 已实现基础版 | `extract(year/month from column)` |
| `GroupBy(x => new { x.OrderTime.Year, Quarter = (x.OrderTime.Month - 1) / 3 + 1 })` | 已实现基础版 | `extract(...)` + 算术表达式 |
| 聚合投影中 `new DateTime(g.Key.Year, g.Key.Month, 1)` | 已实现基础版 | `make_timestamp(...)` |
| 聚合投影中 `new DateTimeOffset(g.Key.Year, g.Key.Month, 1, 0, 0, 0, TimeSpan.Zero)` | 已实现基础版 | `make_timestamptz(..., 'UTC')` |
| 聚合投影后的 `Where/OrderBy/Skip/Take` | 已实现 SQL 预览 | 简单 `Where` 输出 `HAVING`；带排序/分页时使用聚合子查询包装 |
| 聚合投影后的字符串比较/筛选 | 已实现基础版 | 支持 `==` / `!=` / `StartsWith` / `Contains` / `EndsWith` |
| 原生 `HAVING` 关键字输出 | 已实现基础版 | `GROUP BY ... HAVING ...` |
| `CountDistinct/LongCountDistinct` | 已实现基础版 | `count(distinct ...)` |
| `ArrayAgg(selector)` | 已实现基础版 | `array_agg(column)` |
| `JsonbAgg(selector)` | 已实现基础版 | `jsonb_agg(column)::text` |
| 完整 `IGrouping<TKey,T>` materialization | 暂不支持 | 结果形状不直接等价 SQL |

## 10. 原生 SQL

| API | 状态 | 行为 |
| --- | --- | --- |
| `SqlQuery<T>(FormattableString)` | 已实现基础版 | 参数转 `$1/$2` 并可执行实体查询 |
| `SqlCommand(FormattableString)` | 已实现基础版 | 参数转 `$1/$2` 并可执行命令 |
| 原生 SQL 的全局过滤 | 暂不支持自动追加 | 由调用方写入 `WHERE` | `SqlQuery` / `SqlCommand` 不自动应用 `HasQueryFilter`；租户写入校验由应用负责 |
| SQL 注入字符串参数 | 已测试 | 保持参数值 |
| raw string unsafe API | 暂不支持 | 避免误用 |

## 11. 测试覆盖与数量核对

测试数量会随新增用例变化，本文档不将总数作为固定契约。发布审查时可使用以下命令重新统计：

```powershell
rg -n "\[Fact\]|\[Theory\]" tests/Perigon.PostgreSQL.Tests tests/Perigon.PostgreSQL.IntegrationTests
```

上述命令统计测试声明，参数化测试的实际执行数量以测试运行结果为准。另有 NativeAOT publish/run smoke 与 BenchmarkDotNet SQL 生成基线，覆盖：

- 默认映射和特性映射。
- 标识符引用和命名约定。
- 基础 Where。
- nullable/null。
- `DateTime` 与 `DateTimeOffset` 时间范围筛选。
- `DateTimeOffset?` 的 null / `HasValue` / `Value` 时间比较。
- PostgreSQL `timestamp with time zone` 的 UTC 参数归一化。
- session `time zone` 改变后的 `DateTimeOffset` 范围筛选稳定性。
- bool 谓词。
- string 方法。
- string Length/Trim/IsNullOrWhiteSpace。
- string Substring。
- LongCountAsync。
- Order/Skip/Take。
- Count/Any SQL。
- Distinct 标量投影 SQL 和真实 PostgreSQL 执行。
- 数组运算。
- 数组 Any 无谓词和等值谓词。
- 数组 All 等值谓词 SQL 和真实 PostgreSQL 执行。
- JSONB 运算。
- JSONB path exists SQL 和真实 PostgreSQL 执行。
- Insert/Update/Delete SQL。
- SetExpression 表达式更新 SQL 和真实 PostgreSQL 执行。
- 原生 SQL 参数化。
- Select 标量/匿名/DTO 投影 SQL 预览。
- Select DTO 投影和标量投影真实 PostgreSQL 执行。
- IncludeMany split query 父子结构真实 PostgreSQL 执行。
- IncludeMany 子集合过滤和排序真实 PostgreSQL 执行。
- IncludeMany 多父级 `Take` 按父级分页真实 PostgreSQL 执行。
- Inner Join 关联查询 SQL 预览和 DTO 投影真实 PostgreSQL 执行。
- Join inner source 过滤保留。
- Left Join 标准 LINQ 形态 SQL 预览，inner source 过滤进入 ON 条件。
- GroupBy Count 聚合 SQL 预览。
- GroupBy 多 key、MemberInit DTO 投影 SQL 预览和真实 PostgreSQL 执行。
- GroupBy CountDistinct SQL 和真实 PostgreSQL 执行。
- GroupBy ArrayAgg/JsonbAgg SQL 和真实 PostgreSQL 执行。
- GroupBy 简单聚合过滤 HAVING SQL 和真实 PostgreSQL 执行。
- GroupBy 聚合投影后 Where/OrderBy/Skip/Take SQL 预览。
- materializer 基础数值类型转换。
- Docker PostgreSQL 下的 insert returning、实体查询、数组查询、JSONB 查询、update、delete。
- Docker PostgreSQL 下的 CountAsync、AnyAsync、COPY BulkInsert、InsertValues BulkInsert、UpsertMany、原生 SQL 查询和命令执行。
- Docker PostgreSQL 下的 identity 主键 Upsert 和多批次 Bulk 原子回滚。
- Docker PostgreSQL 下的事务提交、异常回滚、嵌套事务拒绝、事务内 COPY、InsertManyReturning。
- Docker PostgreSQL 下的同一 DbContext 并发操作拒绝。
- Docker PostgreSQL 下的事务取消、回滚后连接复用。
- unsupported 表达式诊断。
