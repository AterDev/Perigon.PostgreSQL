# LINQ 支持矩阵

日期：2026-05-24

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
| `Select` 实体投影 | 已实现基础版 | `SELECT columns` | 默认选择实体全部列 |
| `Select` 标量投影 | 已实现基础版 | `SELECT column AS alias` | 通过 `ToScalarListAsync` 执行 |
| `Select` DTO/匿名类型 | 已实现基础版 | `SELECT col1 AS ..., col2 AS ...` | DTO 投影可执行，匿名类型限 SQL 预览 |
| `Distinct` | 已实现基础版 | `SELECT DISTINCT ...` | 支持实体/标量/DTO 投影后的 distinct |
| `Join` | 已实现基础版 | `INNER JOIN` | 支持等值 inner join + DTO 结果投影执行 |
| `GroupJoin` + `SelectMany` + `DefaultIfEmpty` | 已实现 SQL 预览 | `LEFT JOIN` | 标准 LINQ left join 形态，inner filter 放入 ON |
| `SelectMany` | 计划支持 | `JOIN` / `LATERAL` | 复杂形态后置 |

## 2. 执行操作

| API | 状态 | SQL 形态 | 备注 |
| --- | --- | --- | --- |
| `ToQuerySql` | 已实现 | 生成查询 SQL | 测试和诊断 API |
| `ToListAsync` | 已实现基础版 | 执行 SELECT | 当前使用反射 materializer，后续替换为源生成 |
| `FirstOrDefaultAsync` | 已实现基础版 | `LIMIT 1` | 通过 `Take(1)` 实现 |
| `SingleOrDefaultAsync` | 已实现基础版 | `LIMIT 2` | 客户端检测多行 |
| `ToCountSql` | 已实现 | `SELECT count(*)` | 测试和诊断 API |
| `ToAnySql` | 已实现 | `SELECT 1 ... LIMIT 1` | 测试和诊断 API |
| `CountAsync` | 已实现基础版 | `SELECT count(*)` | Docker PostgreSQL 集成测试覆盖 |
| `AnyAsync` | 已实现基础版 | `SELECT 1 ... LIMIT 1` | Docker PostgreSQL 集成测试覆盖 |
| `LongCountAsync` | 已实现基础版 | `SELECT count(*)` | 返回 long |

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
| `BulkInsertAsync` | 已实现基础版 | Binary `COPY FROM STDIN` 或批量 `INSERT ... VALUES` |
| `InsertManyReturningAsync` | 已实现基础版 | 多值 INSERT RETURNING |
| `UpsertManyAsync` | 已实现基础版 | ON CONFLICT |
| `MergeAsync` | 计划支持 | PostgreSQL 15+ MERGE |

## 8. Include 和导航

| API | 状态 | 计划 SQL |
| --- | --- | --- |
| `Include(reference)` | 计划支持 | LEFT JOIN 或 split query |
| `Include(collection)` | 计划支持 | 默认 split query |
| `IncludeManyAsync(parentKey, childForeignKey, resultSelector)` | 已实现基础版 | split query，子查询使用 `ANY($1)` |
| filtered include | 已实现基础版 | `IncludeManyAsync` 子查询支持 `Where/OrderBy/Skip/Take` |
| `ThenInclude` | 计划支持 | 多级 split query |
| `AsSplitQuery` | 计划支持 | 多查询 |
| `AsSingleQuery` | 计划支持 | LEFT JOIN |
| lazy loading | 暂不支持 | 无追踪/AOT 边界 |

## 9. GroupBy 和聚合

| LINQ | 状态 | SQL |
| --- | --- | --- |
| `GroupBy(...).Select(g => new { g.Key, Count = g.Count() })` | 已实现 SQL 预览 | `GROUP BY`, `count(*)` |
| `Sum/Min/Max/Average` | 已实现 SQL 预览 | 聚合函数 |
| 多 key group | 已实现基础版 | 多列 `GROUP BY`，支持 DTO/匿名类型投影 |
| 聚合投影后的 `Where/OrderBy/Skip/Take` | 已实现 SQL 预览 | 简单 `Where` 输出 `HAVING`；带排序/分页时使用聚合子查询包装 |
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
| SQL 注入字符串参数 | 已测试 | 保持参数值 |
| raw string unsafe API | 暂不支持 | 避免误用 |

## 11. 当前测试数量

截至本文档日期，当前测试数量为 132 个，其中包含 103 个单元/SQL 快照测试和 29 个 Docker PostgreSQL 集成测试，覆盖：

- 默认映射和特性映射。
- 标识符引用和命名约定。
- 基础 Where。
- nullable/null。
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
- Docker PostgreSQL 下的事务提交、异常回滚、嵌套事务拒绝、事务内 COPY、InsertManyReturning。
- unsupported 表达式诊断。
