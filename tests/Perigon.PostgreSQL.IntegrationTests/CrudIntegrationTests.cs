using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.RawSql;
using Perigon.PostgreSQL.Update;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CrudIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public CrudIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Insert_returning_and_query_roundtrip()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(EntityModel.For<IntegrationUser>().IsGenerated);
        Assert.True(EntityMaterializerRegistry.TryGet<IntegrationUser>(out _));
    Assert.True(EntityValueAccessorRegistry.TryGetAccessor(typeof(IntegrationUser), nameof(IntegrationUser.UserName), out _));

        var inserted = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Alice",
            Age = 30,
            IsActive = true,
            Status = "active",
            CreatedAt = createdAt,
            Tags = ["developer", "postgres"],
            ProfileJson = """{"level":3,"name":"Alice"}"""
        });

        Assert.True(inserted.Id > 0);
        Assert.Equal("Alice", inserted.UserName);
        Assert.NotNull(inserted.Tags);
        Assert.Equal(["developer", "postgres"], inserted.Tags);

        var users = await db.IntegrationUsers
            .Where(u => u.Id == inserted.Id && u.Tags!.Contains("postgres"))
            .ToListAsync();

        Assert.Single(users);
        Assert.Equal("Alice", users[0].UserName);
    }

    [Fact]
    public async Task Update_and_delete_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var inserted = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Bob",
            Age = 41,
            IsActive = true,
            Status = "active",
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["ops"],
            ProfileJson = """{"level":1}"""
        });

        var updated = await db.IntegrationUsers
            .Where(u => u.Id == inserted.Id)
            .ExecuteUpdateAsync(s => s.Set(u => u.Status, "retired"));

        Assert.Equal(1, updated);

        var afterUpdate = await db.IntegrationUsers.Where(u => u.Id == inserted.Id).ToListAsync();
        Assert.Equal("retired", afterUpdate.Single().Status);

        var deleted = await db.IntegrationUsers
            .Where(u => u.Id == inserted.Id)
            .ExecuteDeleteAsync();

        Assert.Equal(1, deleted);
        Assert.Empty(await db.IntegrationUsers.Where(u => u.Id == inserted.Id).ToListAsync());
    }

    [Fact]
    public async Task Jsonb_filter_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Carol",
            Age = 25,
            IsActive = true,
            Status = "active",
            CreatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["json"],
            ProfileJson = """{"level":9,"name":"Carol"}"""
        });

        var result = await db.IntegrationUsers
            .Where(u => u.ProfileJson.JsonbText("name") == "Carol")
            .ToListAsync();

        Assert.Contains(result, u => u.UserName == "Carol");
    }

    [Fact]
    public async Task Jsonb_contains_and_has_key_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Jsonb-Contains",
                Age = 26,
                IsActive = true,
                Status = "jsonb-operators",
                CreatedAt = new DateTime(2026, 1, 18, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["jsonb"],
                ProfileJson = """{"level":3,"name":"Jsonb-Contains","team":"platform"}"""
            },
            new IntegrationUser
            {
                UserName = "Jsonb-Miss",
                Age = 27,
                IsActive = true,
                Status = "jsonb-operators",
                CreatedAt = new DateTime(2026, 1, 19, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["jsonb"],
                ProfileJson = """{"name":"Jsonb-Miss"}"""
            }
        ]);

        var contains = await db.IntegrationUsers
            .Where(u => u.Status == "jsonb-operators" && u.ProfileJson.JsonbContains("""{"level":3}"""))
            .Select(u => u.UserName)
            .ToScalarListAsync();

        var hasKey = await db.IntegrationUsers
            .Where(u => u.Status == "jsonb-operators" && u.ProfileJson.JsonbHasKey("team"))
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["Jsonb-Contains"], contains);
        Assert.Equal(["Jsonb-Contains"], hasKey);
    }

    [Fact]
    public async Task Count_and_any_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Dora",
            Age = 28,
            IsActive = true,
            Status = "counted",
            CreatedAt = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["count"],
            ProfileJson = """{"level":2}"""
        });

        var count = await db.IntegrationUsers.Where(u => u.Status == "counted").CountAsync();
        var longCount = await db.IntegrationUsers.Where(u => u.Status == "counted").LongCountAsync();
        var any = await db.IntegrationUsers.Where(u => u.Status == "counted").AnyAsync();
        var none = await db.IntegrationUsers.Where(u => u.Status == "missing-status").AnyAsync();

        Assert.True(count >= 1);
        Assert.True(longCount >= 1);
        Assert.True(any);
        Assert.False(none);
    }

    [Fact]
    public async Task Expression_update_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var inserted = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Expression-Update",
            Age = 50,
            IsActive = true,
            Status = "expression-update",
            CreatedAt = new DateTime(2026, 1, 13, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["expression"],
            ProfileJson = """{"update":"expression"}"""
        });

        var updated = await db.IntegrationUsers
            .Where(u => u.Id == inserted.Id)
            .ExecuteUpdateAsync(s => s.SetExpression(u => u.Age, u => u.Age + 2));

        Assert.Equal(1, updated);
        var after = await db.IntegrationUsers.Where(u => u.Id == inserted.Id).ToListAsync();
        Assert.Equal(52, after.Single().Age);
    }

    [Fact]
    public async Task String_substring_and_array_any_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "String-Alice",
            Age = 35,
            IsActive = true,
            Status = "string-array",
            CreatedAt = new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["linq", "array-any"],
            ProfileJson = """{"string":true}"""
        });

        var users = await db.IntegrationUsers
            .Where(u => u.UserName.Substring(0, 6) == "String" && u.Tags!.Any(t => t == "array-any"))
            .ToListAsync();

        Assert.Contains(users, u => u.UserName == "String-Alice");
    }

    [Fact]
    public async Task Array_all_equality_predicate_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Array-All-Match",
                Age = 36,
                IsActive = true,
                Status = "array-all",
                CreatedAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["same", "same"],
                ProfileJson = """{"arrayAll":true}"""
            },
            new IntegrationUser
            {
                UserName = "Array-All-Empty",
                Age = 37,
                IsActive = true,
                Status = "array-all",
                CreatedAt = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
                Tags = [],
                ProfileJson = """{"arrayAll":true}"""
            },
            new IntegrationUser
            {
                UserName = "Array-All-Miss",
                Age = 38,
                IsActive = true,
                Status = "array-all",
                CreatedAt = new DateTime(2026, 1, 17, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["same", "other"],
                ProfileJson = """{"arrayAll":false}"""
            }
        ]);

        var users = await db.IntegrationUsers
            .Where(u => u.Status == "array-all" && u.Tags!.All(t => t == "same"))
            .OrderBy(u => u.UserName)
            .ToListAsync();

        Assert.Equal(["Array-All-Empty", "Array-All-Match"], users.Select(u => u.UserName).ToArray());
    }

    [Fact]
    public async Task Array_operators_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var requiredTags = new[] { "postgres", "aot" };
        var allowedTags = new[] { "postgres", "aot", "linq" };
        var overlappingTags = new[] { "aot", "missing" };
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Array-Operators-Full",
                Age = 39,
                IsActive = true,
                Status = "array-operators",
                CreatedAt = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["postgres", "aot", "linq"],
                ProfileJson = """{"arrayOps":"full"}"""
            },
            new IntegrationUser
            {
                UserName = "Array-Operators-Subset",
                Age = 40,
                IsActive = true,
                Status = "array-operators",
                CreatedAt = new DateTime(2026, 1, 21, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["postgres"],
                ProfileJson = """{"arrayOps":"subset"}"""
            },
            new IntegrationUser
            {
                UserName = "Array-Operators-Other",
                Age = 41,
                IsActive = true,
                Status = "array-operators",
                CreatedAt = new DateTime(2026, 1, 22, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["other"],
                ProfileJson = """{"arrayOps":"other"}"""
            }
        ]);

        var containsAll = await db.IntegrationUsers
            .Where(u => u.Status == "array-operators" && u.Tags!.ContainsAll(requiredTags))
            .Select(u => u.UserName)
            .ToScalarListAsync();

        var containedBy = await db.IntegrationUsers
            .Where(u => u.Status == "array-operators" && u.Tags!.IsContainedBy(allowedTags))
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        var overlaps = await db.IntegrationUsers
            .Where(u => u.Status == "array-operators" && u.Tags!.Overlaps(overlappingTags))
            .Select(u => u.UserName)
            .ToScalarListAsync();

        var nonEmpty = await db.IntegrationUsers
            .Where(u => u.Status == "array-operators" && u.Tags!.Any() && u.Tags!.Length > 1)
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["Array-Operators-Full"], containsAll);
        Assert.Equal(["Array-Operators-Full", "Array-Operators-Subset"], containedBy);
        Assert.Equal(["Array-Operators-Full"], overlaps);
        Assert.Equal(["Array-Operators-Full"], nonEmpty);
    }

    [Fact]
    public async Task Bulk_insert_copy_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var rows = Enumerable.Range(1, 5)
            .Select(i => new IntegrationUser
            {
                UserName = "Bulk-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Age = 20 + i,
                IsActive = i % 2 == 0,
                Status = "bulk",
                CreatedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                Tags = ["bulk", "copy"],
                ProfileJson = """{"source":"copy"}"""
            })
            .ToArray();

        await db.IntegrationUsers.BulkInsertAsync(rows);

        var count = await db.IntegrationUsers.Where(u => u.Status == "bulk").CountAsync();
        var copied = await db.IntegrationUsers
            .Where(u => u.Tags!.Contains("copy"))
            .OrderBy(u => u.UserName)
            .ToListAsync();

        Assert.True(count >= 5);
        Assert.True(copied.Count >= 5);
    }

    [Fact]
    public async Task Bulk_insert_values_executes_against_postgres_and_respects_batch_size()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var rows = Enumerable.Range(1, 4)
            .Select(i => new IntegrationUser
            {
                UserName = "Bulk-Values-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Age = 30 + i,
                IsActive = i % 2 == 0,
                Status = "bulk-values",
                CreatedAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                Tags = ["bulk", "values"],
                ProfileJson = """{"source":"insert-values"}"""
            })
            .ToArray();

        await db.IntegrationUsers.BulkInsertAsync(
            rows,
            new Perigon.PostgreSQL.Bulk.BulkInsertOptions
            {
                Mode = Perigon.PostgreSQL.Bulk.BulkInsertMode.InsertValues,
                BatchSize = 2
            });

        var inserted = await db.IntegrationUsers
            .Where(u => u.Status == "bulk-values")
            .OrderBy(u => u.UserName)
            .ToListAsync();

        Assert.Equal(4, inserted.Count);
        Assert.All(inserted, row => Assert.Contains("values", row.Tags!));
    }

    [Fact]
    public async Task Upsert_many_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var initial = new IntegrationUser
        {
            UserName = "Upsert-Alice",
            Age = 31,
            IsActive = true,
            Status = "initial",
            CreatedAt = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["upsert"],
            ProfileJson = """{"version":1}"""
        };

        var inserted = await db.IntegrationUsers.InsertAsync(initial);
        var affected = await db.IntegrationUsers.UpsertManyAsync(
            [
                new IntegrationUser
                {
                    UserName = "Upsert-Alice",
                    Age = 32,
                    IsActive = false,
                    Status = "updated",
                    CreatedAt = inserted.CreatedAt,
                    Tags = ["upsert", "updated"],
                    ProfileJson = """{"version":2}"""
                }
            ],
            u => u.UserName);

        Assert.Equal(1, affected);
        var after = await db.IntegrationUsers.Where(u => u.UserName == "Upsert-Alice").ToListAsync();
        var user = Assert.Single(after);
        Assert.Equal(32, user.Age);
        Assert.Equal("updated", user.Status);
    }

    [Fact]
    public async Task Upsert_many_can_do_nothing_on_conflict_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Upsert-DoNothing",
            Age = 44,
            IsActive = true,
            Status = "upsert-existing",
            CreatedAt = new DateTime(2026, 1, 23, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["upsert", "existing"],
            ProfileJson = """{"version":1}"""
        });

        var affected = await db.IntegrationUsers.UpsertManyAsync(
            [
                new IntegrationUser
                {
                    UserName = "Upsert-DoNothing",
                    Age = 99,
                    IsActive = false,
                    Status = "upsert-new",
                    CreatedAt = new DateTime(2026, 1, 24, 0, 0, 0, DateTimeKind.Utc),
                    Tags = ["upsert", "new"],
                    ProfileJson = """{"version":2}"""
                }
            ],
            u => u.UserName,
            new Perigon.PostgreSQL.Bulk.UpsertOptions<IntegrationUser> { DoNothing = true });

        var after = await db.IntegrationUsers.Where(u => u.UserName == "Upsert-DoNothing").ToListAsync();
        var user = Assert.Single(after);

        Assert.Equal(0, affected);
        Assert.Equal(44, user.Age);
        Assert.Equal("upsert-existing", user.Status);
    }

    [Fact]
    public async Task Raw_sql_query_and_command_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Raw-Alice",
            Age = 29,
            IsActive = true,
            Status = "raw",
            CreatedAt = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["raw"],
            ProfileJson = """{"source":"raw"}"""
        });

        var users = await db
            .SqlQuery<IntegrationUser>($"select id, user_name, age, is_active, status, created_at, updated_at, tags, profile_json from integration_users where user_name = {"Raw-Alice"}")
            .ToListAsync();

        Assert.Single(users);

        var updated = await db
            .SqlCommand($"update integration_users set status = {"raw-updated"} where user_name = {"Raw-Alice"}")
            .ExecuteAsync();

        Assert.Equal(1, updated);
        var after = await db.IntegrationUsers.Where(u => u.UserName == "Raw-Alice").ToListAsync();
        Assert.Equal("raw-updated", after.Single().Status);
    }

    [Fact]
    public async Task Raw_sql_parameterization_prevents_injection_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Raw-Safe",
            Age = 30,
            IsActive = true,
            Status = "raw-safe",
            CreatedAt = new DateTime(2026, 1, 25, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["raw", "safe"],
            ProfileJson = """{"source":"safe"}"""
        });

        const string payload = "' OR 1=1 --";

        var users = await db
            .SqlQuery<IntegrationUser>($"select id, user_name, age, is_active, status, created_at, updated_at, tags, profile_json from integration_users where user_name = {payload}")
            .ToListAsync();

        var updated = await db
            .SqlCommand($"update integration_users set status = {"injected"} where user_name = {payload}")
            .ExecuteAsync();

        var after = await db.IntegrationUsers.Where(u => u.UserName == "Raw-Safe").ToListAsync();

        Assert.Empty(users);
        Assert.Equal(0, updated);
        Assert.Equal("raw-safe", after.Single().Status);
    }

    [Fact]
    public async Task Insert_many_returning_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var rows = new[]
        {
            new IntegrationUser
            {
                UserName = "InsertMany-A",
                Age = 33,
                IsActive = true,
                Status = "insert-many",
                CreatedAt = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["insert-many"],
                ProfileJson = """{"batch":1}"""
            },
            new IntegrationUser
            {
                UserName = "InsertMany-B",
                Age = 34,
                IsActive = false,
                Status = "insert-many",
                CreatedAt = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["insert-many"],
                ProfileJson = """{"batch":1}"""
            }
        };

        var inserted = await db.IntegrationUsers.InsertManyReturningAsync(rows);

        Assert.Equal(2, inserted.Count);
        Assert.All(inserted, row => Assert.True(row.Id > 0));
    }

    [Fact]
    public async Task Insert_many_returning_respects_batch_size()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var rows = Enumerable.Range(1, 3)
            .Select(i => new IntegrationUser
            {
                UserName = "InsertMany-Batch-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Age = 40 + i,
                IsActive = true,
                Status = "insert-many-batch",
                CreatedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                Tags = ["insert-many", "batch"],
                ProfileJson = """{"batch":2}"""
            })
            .ToArray();

        var inserted = await db.IntegrationUsers.InsertManyReturningAsync(
            rows,
            new Perigon.PostgreSQL.Bulk.ReturningOptions<IntegrationUser> { BatchSize = 1 });

        Assert.Equal(3, inserted.Count);
        Assert.All(inserted, row => Assert.True(row.Id > 0));
    }

    [Fact]
    public async Task Dto_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Projection-Alice",
            Age = 36,
            IsActive = true,
            Status = "projection",
            CreatedAt = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["projection"],
            ProfileJson = """{"projection":true}"""
        });

        var summaries = await db.IntegrationUsers
            .Where(u => u.Status == "projection")
            .OrderBy(u => u.UserName)
            .Select(u => new UserSummary { Id = u.Id, UserName = u.UserName, Age = u.Age })
            .ToListAsync();

        var summary = Assert.Single(summaries);
        Assert.True(summary.Id > 0);
        Assert.Equal("Projection-Alice", summary.UserName);
        Assert.Equal(36, summary.Age);
    }

    [Fact]
    public async Task Scalar_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Scalar-Alice",
            Age = 37,
            IsActive = true,
            Status = "scalar",
            CreatedAt = new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["scalar"],
            ProfileJson = """{"scalar":true}"""
        });

        var names = await db.IntegrationUsers
            .Where(u => u.Status == "scalar")
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["Scalar-Alice"], names);
    }

    [Fact]
    public async Task First_or_default_and_single_or_default_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Single-Match",
            Age = 42,
            IsActive = true,
            Status = "single-operators",
            CreatedAt = new DateTime(2026, 1, 26, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["single"],
            ProfileJson = """{"single":1}"""
        });

        var first = await db.IntegrationUsers
            .Where(u => u.Status == "single-operators")
            .OrderBy(u => u.UserName)
            .FirstOrDefaultAsync();

        var single = await db.IntegrationUsers
            .Where(u => u.UserName == "Single-Match")
            .SingleOrDefaultAsync();

        var missing = await db.IntegrationUsers
            .Where(u => u.UserName == "Single-Missing")
            .SingleOrDefaultAsync();

        Assert.NotNull(first);
        Assert.Equal("Single-Match", first!.UserName);
        Assert.NotNull(single);
        Assert.Equal("Single-Match", single!.UserName);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Single_or_default_throws_when_multiple_rows_match()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Single-Multi-A",
                Age = 45,
                IsActive = true,
                Status = "single-multi",
                CreatedAt = new DateTime(2026, 1, 27, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["single", "multi"],
                ProfileJson = """{"single":2}"""
            },
            new IntegrationUser
            {
                UserName = "Single-Multi-B",
                Age = 46,
                IsActive = true,
                Status = "single-multi",
                CreatedAt = new DateTime(2026, 1, 28, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["single", "multi"],
                ProfileJson = """{"single":3}"""
            }
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.IntegrationUsers
                .Where(u => u.Status == "single-multi")
                .SingleOrDefaultAsync());
    }
}
