using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.RawSql;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class QueryShapeIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public QueryShapeIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Inner_join_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var user = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Join-Projection",
            Age = 44,
            IsActive = true,
            Status = "join-projection",
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["join"],
            ProfileJson = """{"join":true}"""
        });

        var blog = await db.IntegrationBlogs.InsertAsync(new IntegrationBlog
        {
            IntegrationUserId = user.Id,
            Name = "Join Blog",
            IsPublic = true,
            CreatedAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc)
        });

        var rows = await db.IntegrationUsers
            .Where(u => u.UserName == "Join-Projection")
            .Join(
                db.IntegrationBlogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.IntegrationUserId,
                (u, b) => new IntegrationUserBlogRow
                {
                    UserId = u.Id,
                    UserName = u.UserName,
                    BlogId = b.Id,
                    BlogName = b.Name
                })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal("Join-Projection", row.UserName);
        Assert.Equal(blog.Id, row.BlogId);
        Assert.Equal("Join Blog", row.BlogName);
    }

    [Fact]
    public async Task Left_join_projection_preserves_rows_without_matches()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var withBlog = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "LeftJoin-WithBlog",
            Age = 35,
            IsActive = true,
            Status = "left-join",
            CreatedAt = new DateTime(2026, 4, 17, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["left-join"],
            ProfileJson = """{"leftJoin":"with-blog"}"""
        });

        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "LeftJoin-NoBlog",
            Age = 36,
            IsActive = true,
            Status = "left-join",
            CreatedAt = new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["left-join"],
            ProfileJson = """{"leftJoin":"no-blog"}"""
        });

        _ = await db.IntegrationBlogs.InsertAsync(new IntegrationBlog
        {
            IntegrationUserId = withBlog.Id,
            Name = "Left Join Blog",
            IsPublic = true,
            CreatedAt = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc)
        });

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "left-join")
            .OrderBy(u => u.UserName)
            .GroupJoin(
                db.IntegrationBlogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.IntegrationUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new IntegrationLeftJoinRow
                {
                    UserName = x.u.UserName,
                    BlogName = b!.Name
                })
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.UserName == "LeftJoin-WithBlog" && row.BlogName == "Left Join Blog");
        Assert.Contains(rows, row => row.UserName == "LeftJoin-NoBlog" && row.BlogName is null);
    }

    [Fact]
    public async Task Ordered_paging_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Paging-A",
                Age = 21,
                IsActive = true,
                Status = "paging",
                CreatedAt = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["paging"],
                ProfileJson = """{"paging":1}"""
            },
            new IntegrationUser
            {
                UserName = "Paging-B",
                Age = 24,
                IsActive = true,
                Status = "paging",
                CreatedAt = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["paging"],
                ProfileJson = """{"paging":2}"""
            },
            new IntegrationUser
            {
                UserName = "Paging-C",
                Age = 24,
                IsActive = true,
                Status = "paging",
                CreatedAt = new DateTime(2026, 4, 22, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["paging"],
                ProfileJson = """{"paging":3}"""
            },
            new IntegrationUser
            {
                UserName = "Paging-D",
                Age = 29,
                IsActive = true,
                Status = "paging",
                CreatedAt = new DateTime(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["paging"],
                ProfileJson = """{"paging":4}"""
            }
        ]);

        var names = await db.IntegrationUsers
            .Where(u => u.Status == "paging")
            .OrderByDescending(u => u.Age)
            .ThenBy(u => u.UserName)
            .Skip(1)
            .Take(2)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["Paging-B", "Paging-C"], names);
    }

    [Fact]
    public async Task Null_filter_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "NullFilter-Null",
                Age = 33,
                IsActive = true,
                Status = "null-filter",
                CreatedAt = new DateTime(2026, 4, 24, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = null,
                Tags = ["null-filter"],
                ProfileJson = """{"updated":null}"""
            },
            new IntegrationUser
            {
                UserName = "NullFilter-Set",
                Age = 34,
                IsActive = true,
                Status = "null-filter",
                CreatedAt = new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["null-filter"],
                ProfileJson = """{"updated":"set"}"""
            }
        ]);

        var pending = await db.IntegrationUsers
            .Where(u => u.Status == "null-filter" && u.UpdatedAt == null)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        var updated = await db.IntegrationUsers
            .Where(u => u.Status == "null-filter" && u.UpdatedAt != null)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["NullFilter-Null"], pending);
        Assert.Equal(["NullFilter-Set"], updated);
    }

    [Fact]
    public async Task Negated_nullable_has_value_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "NullHasValue-Null",
                Age = 35,
                IsActive = true,
                Status = "null-has-value",
                CreatedAt = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = null,
                Tags = ["null-has-value"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "NullHasValue-Set",
                Age = 36,
                IsActive = true,
                Status = "null-has-value",
                CreatedAt = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["null-has-value"],
                ProfileJson = "{}"
            }
        ]);

        var missingUpdatedAt = await db.IntegrationUsers
            .Where(u => u.Status == "null-has-value" && !u.UpdatedAt.HasValue)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["NullHasValue-Null"], missingUpdatedAt);
    }

    [Fact]
    public async Task DateTime_range_filter_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "DateRange-Before",
                Age = 31,
                IsActive = true,
                Status = "date-range",
                CreatedAt = new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                Tags = ["date-range"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "DateRange-In",
                Age = 32,
                IsActive = true,
                Status = "date-range",
                CreatedAt = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
                Tags = ["date-range"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "DateRange-After",
                Age = 33,
                IsActive = true,
                Status = "date-range",
                CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["date-range"],
                ProfileJson = "{}"
            }
        ]);

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var names = await db.IntegrationUsers
            .Where(u => u.Status == "date-range" && u.CreatedAt >= start && u.CreatedAt < end)
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName)
            .ToScalarListAsync();

        Assert.Equal(["DateRange-In"], names);
    }

    [Fact]
    public async Task DateTimeOffset_range_filter_matches_equivalent_instants_across_offsets()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationOffsetOrders.InsertManyReturningAsync(
        [
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-BEFORE",
                Status = "offset-range",
                OrderTime = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero),
                TotalPrice = 10m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-IN-UTC",
                Status = "offset-range",
                OrderTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
                TotalPrice = 20m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-IN-PLUS8",
                Status = "offset-range",
                OrderTime = new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.FromHours(8)),
                TotalPrice = 30m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-AFTER",
                Status = "offset-range",
                OrderTime = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                TotalPrice = 40m
            }
        ]);

        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var end = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8));

        var orderNos = await db.IntegrationOffsetOrders
            .Where(o => o.Status == "offset-range" && o.OrderTime >= start && o.OrderTime < end)
            .OrderBy(o => o.OrderNo)
            .Select(o => o.OrderNo)
            .ToScalarListAsync();

        Assert.Equal(["TZ-IN-PLUS8", "TZ-IN-UTC"], orderNos);
    }

    [Fact]
    public async Task DateTimeOffset_range_filter_remains_stable_when_session_timezone_changes()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);

        _ = await db.IntegrationOffsetOrders.InsertManyReturningAsync(
        [
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-SESSION-IN",
                Status = "offset-session-range",
                OrderTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
                TotalPrice = 50m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "TZ-SESSION-OUT",
                Status = "offset-session-range",
                OrderTime = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                TotalPrice = 60m
            }
        ]);

        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var end = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8));

        await db.TransactionAsync(async ct =>
        {
            await db.SqlCommand(System.Runtime.CompilerServices.FormattableStringFactory.Create("set local time zone 'Asia/Shanghai'"))
                .ExecuteAsync(ct);

            var orderNos = await db.IntegrationOffsetOrders
                .Where(o => o.Status == "offset-session-range" && o.OrderTime >= start && o.OrderTime < end)
                .OrderBy(o => o.OrderNo)
                .Select(o => o.OrderNo)
                .ToScalarListAsync(ct);

            Assert.Equal(["TZ-SESSION-IN"], orderNos);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Nullable_DateTimeOffset_range_filter_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationOffsetCheckpoints.InsertManyReturningAsync(
        [
            new IntegrationOffsetCheckpoint
            {
                CheckpointNo = "CP-NULL",
                Status = "offset-nullable-range",
                ProcessedAt = null
            },
            new IntegrationOffsetCheckpoint
            {
                CheckpointNo = "CP-START",
                Status = "offset-nullable-range",
                ProcessedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new IntegrationOffsetCheckpoint
            {
                CheckpointNo = "CP-MID",
                Status = "offset-nullable-range",
                ProcessedAt = new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.FromHours(8))
            },
            new IntegrationOffsetCheckpoint
            {
                CheckpointNo = "CP-END",
                Status = "offset-nullable-range",
                ProcessedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
            }
        ]);

        var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var end = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8));

        var present = await db.IntegrationOffsetCheckpoints
            .Where(c => c.Status == "offset-nullable-range" && c.ProcessedAt.HasValue && c.ProcessedAt.Value >= start && c.ProcessedAt.Value < end)
            .OrderBy(c => c.CheckpointNo)
            .Select(c => c.CheckpointNo)
            .ToScalarListAsync();

        var missing = await db.IntegrationOffsetCheckpoints
            .Where(c => c.Status == "offset-nullable-range" && !c.ProcessedAt.HasValue)
            .Select(c => c.CheckpointNo)
            .ToScalarListAsync();

        Assert.Equal(["CP-MID", "CP-START"], present);
        Assert.Equal(["CP-NULL"], missing);
    }

    [Fact]
    public async Task Multi_key_group_by_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        Assert.True(EntityMaterializerRegistry.TryGet<IntegrationUserStatusStat>(out _));

        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Group-Multi-A",
                Age = 20,
                IsActive = true,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":1}"""
            },
            new IntegrationUser
            {
                UserName = "Group-Multi-B",
                Age = 30,
                IsActive = true,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":2}"""
            },
            new IntegrationUser
            {
                UserName = "Group-Multi-C",
                Age = 40,
                IsActive = false,
                Status = "group-multi",
                CreatedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["group"],
                ProfileJson = """{"group":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "group-multi")
            .GroupBy(u => new { u.Status, u.IsActive })
            .Select(g => new IntegrationUserStatusStat
            {
                Status = g.Key.Status,
                IsActive = g.Key.IsActive,
                Count = g.LongCount(),
                AverageAge = g.Average(u => u.Age)
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.IsActive)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        var active = rows.Single(row => row.IsActive);
        Assert.Equal("group-multi", active.Status);
        Assert.Equal(2, active.Count);
        Assert.Equal(25d, active.AverageAge);

        var inactive = rows.Single(row => !row.IsActive);
        Assert.Equal(1, inactive.Count);
        Assert.Equal(40d, inactive.AverageAge);
    }

    [Fact]
    public async Task Distinct_scalar_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Distinct-A",
                Age = 22,
                IsActive = true,
                Status = "distinct-status",
                CreatedAt = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["distinct"],
                ProfileJson = """{"distinct":1}"""
            },
            new IntegrationUser
            {
                UserName = "Distinct-B",
                Age = 23,
                IsActive = true,
                Status = "distinct-status",
                CreatedAt = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["distinct"],
                ProfileJson = """{"distinct":2}"""
            }
        ]);

        var statuses = await db.IntegrationUsers
            .Where(u => u.Tags!.Contains("distinct"))
            .Select(u => u.Status)
            .Distinct()
            .ToScalarListAsync();

        Assert.Equal(["distinct-status"], statuses);
    }

    [Fact]
    public async Task Count_distinct_group_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "CountDistinct-A",
                Age = 24,
                IsActive = true,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":1}"""
            },
            new IntegrationUser
            {
                UserName = "CountDistinct-B",
                Age = 25,
                IsActive = false,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 9, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":2}"""
            },
            new IntegrationUser
            {
                UserName = "CountDistinct-C",
                Age = 26,
                IsActive = false,
                Status = "count-distinct",
                CreatedAt = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["count-distinct"],
                ProfileJson = """{"countDistinct":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "count-distinct")
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationDistinctStatusStat
            {
                Status = g.Key,
                DistinctActiveStates = g.LongCountDistinct(u => u.IsActive)
            })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("count-distinct", row.Status);
        Assert.Equal(2, row.DistinctActiveStates);
    }

    [Fact]
    public async Task Native_aggregate_group_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "NativeAgg-A",
                Age = 27,
                IsActive = true,
                Status = "native-aggregate",
                CreatedAt = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["native-aggregate"],
                ProfileJson = """{"nativeAgg":1}"""
            },
            new IntegrationUser
            {
                UserName = "NativeAgg-B",
                Age = 28,
                IsActive = true,
                Status = "native-aggregate",
                CreatedAt = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["native-aggregate"],
                ProfileJson = """{"nativeAgg":2}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status == "native-aggregate")
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationAggregatePayload
            {
                Status = g.Key,
                Names = g.ArrayAgg(u => u.UserName),
                Profiles = g.JsonbAgg(u => u.ProfileJson)
            })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("native-aggregate", row.Status);
        Assert.Equal(["NativeAgg-A", "NativeAgg-B"], row.Names.Order().ToArray());
        Assert.Contains("\"nativeAgg\": 1", row.Profiles, StringComparison.Ordinal);
        Assert.Contains("\"nativeAgg\": 2", row.Profiles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_by_having_projection_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "Having-A",
                Age = 29,
                IsActive = true,
                Status = "having-match",
                CreatedAt = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":1}"""
            },
            new IntegrationUser
            {
                UserName = "Having-B",
                Age = 30,
                IsActive = true,
                Status = "having-match",
                CreatedAt = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":2}"""
            },
            new IntegrationUser
            {
                UserName = "Having-C",
                Age = 31,
                IsActive = true,
                Status = "having-miss",
                CreatedAt = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having"],
                ProfileJson = """{"having":3}"""
            }
        ]);

        var rows = await db.IntegrationUsers
            .Where(u => u.Status!.StartsWith("having-"))
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationUserStatusStat
            {
                Status = g.Key,
                Count = g.LongCount()
            })
            .Where(x => x.Count >= 2)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("having-match", row.Status);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task Group_by_having_null_filter_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertManyReturningAsync(
        [
            new IntegrationUser
            {
                UserName = "HavingNull-A",
                Age = 37,
                IsActive = true,
                Status = null,
                CreatedAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having-null"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "HavingNull-B",
                Age = 38,
                IsActive = false,
                Status = null,
                CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having-null"],
                ProfileJson = "{}"
            },
            new IntegrationUser
            {
                UserName = "HavingNull-C",
                Age = 39,
                IsActive = true,
                Status = "having-null-non-null",
                CreatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["having-null"],
                ProfileJson = "{}"
            }
        ]);

        string? status = null;

        var rows = await db.IntegrationUsers
            .Where(u => u.Tags!.Contains("having-null"))
            .GroupBy(u => u.Status)
            .Select(g => new IntegrationUserStatusStat
            {
                Status = g.Key,
                Count = g.LongCount()
            })
            .Where(x => x.Status == status)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Null(row.Status);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task Jsonb_path_exists_executes_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "JsonPath-Alice",
            Age = 45,
            IsActive = true,
            Status = "jsonpath",
            CreatedAt = new DateTime(2026, 4, 11, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["jsonpath"],
            ProfileJson = """{"level":7,"name":"JsonPath-Alice"}"""
        });

        var users = await db.IntegrationUsers
            .Where(u => u.ProfileJson.JsonbPathExists("$.level ? (@ > 5)"))
            .ToListAsync();

        Assert.Contains(users, u => u.UserName == "JsonPath-Alice");
    }

    [Fact]
    public async Task Daily_monthly_and_quarterly_statistics_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationOrders.InsertManyReturningAsync(
        [
            new IntegrationOrder
            {
                OrderNo = "S-001",
                Status = "paid",
                OrderTime = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc),
                TotalPrice = 10.5m
            },
            new IntegrationOrder
            {
                OrderNo = "S-002",
                Status = "paid",
                OrderTime = new DateTime(2026, 1, 15, 16, 45, 0, DateTimeKind.Utc),
                TotalPrice = 15.0m
            },
            new IntegrationOrder
            {
                OrderNo = "S-003",
                Status = "pending",
                OrderTime = new DateTime(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc),
                TotalPrice = 20m
            },
            new IntegrationOrder
            {
                OrderNo = "S-004",
                Status = "paid-quarter",
                OrderTime = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc),
                TotalPrice = 30m
            },
            new IntegrationOrder
            {
                OrderNo = "S-005",
                Status = "paid-quarter",
                OrderTime = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc),
                TotalPrice = 40m
            }
        ]);

        var daily = await db.IntegrationOrders
            .GroupBy(o => o.OrderTime.Date)
            .Select(g => new IntegrationSummaryLineChartRow
            {
                Date = g.Key,
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var monthly = await db.IntegrationOrders
            .GroupBy(o => new { o.OrderTime.Year, o.OrderTime.Month })
            .Select(g => new IntegrationSummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, g.Key.Month, 1),
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var quarterly = await db.IntegrationOrders
            .GroupBy(o => new { o.OrderTime.Year, Quarter = (o.OrderTime.Month - 1) / 3 + 1 })
            .Select(g => new IntegrationSummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1),
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        Assert.Contains(daily, row => row.Date == new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) && row.Count == 2 && row.TotalAmount == 25.5m);
        Assert.Contains(monthly, row => row.Date == new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) && row.Count == 2 && row.TotalAmount == 25.5m);
        Assert.Contains(monthly, row => row.Date == new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Unspecified) && row.Count == 1 && row.TotalAmount == 20m);
        Assert.Contains(quarterly, row => row.Date == new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) && row.Count == 3 && row.TotalAmount == 45.5m);
        Assert.Contains(quarterly, row => row.Date == new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Unspecified) && row.Count == 2 && row.TotalAmount == 70m);
    }

    [Fact]
    public async Task Grouped_statistics_support_string_filter_after_projection()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationOrders.InsertManyReturningAsync(
        [
            new IntegrationOrder
            {
                OrderNo = "S-101",
                Status = "paid-success",
                OrderTime = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                TotalPrice = 11m
            },
            new IntegrationOrder
            {
                OrderNo = "S-102",
                Status = "paid-success",
                OrderTime = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
                TotalPrice = 12m
            },
            new IntegrationOrder
            {
                OrderNo = "S-103",
                Status = "pending-review",
                OrderTime = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
                TotalPrice = 13m
            }
        ]);

        var rows = await db.IntegrationOrders
            .GroupBy(o => new { o.OrderTime.Year, o.Status })
            .Select(g => new IntegrationSummaryLineChartRow
            {
                Date = new DateTime(g.Key.Year, 1, 1),
                Status = g.Key.Status,
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .Where(x => x.Status!.StartsWith("paid") && x.Count > 1)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("paid-success", row.Status);
        Assert.Equal(2, row.Count);
        Assert.Equal(23m, row.TotalAmount);
    }

    [Fact]
    public async Task DateTimeOffset_daily_monthly_and_quarterly_statistics_execute_against_postgres()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        _ = await db.IntegrationOffsetOrders.InsertManyReturningAsync(
        [
            new IntegrationOffsetOrder
            {
                OrderNo = "O-001",
                Status = "paid",
                OrderTime = new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.Zero),
                TotalPrice = 10.5m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "O-002",
                Status = "paid",
                OrderTime = new DateTimeOffset(2026, 1, 15, 16, 45, 0, TimeSpan.Zero),
                TotalPrice = 15.0m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "O-003",
                Status = "pending",
                OrderTime = new DateTimeOffset(2026, 2, 20, 9, 0, 0, TimeSpan.Zero),
                TotalPrice = 20m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "O-004",
                Status = "paid-quarter",
                OrderTime = new DateTimeOffset(2026, 5, 10, 11, 0, 0, TimeSpan.Zero),
                TotalPrice = 30m
            },
            new IntegrationOffsetOrder
            {
                OrderNo = "O-005",
                Status = "paid-quarter",
                OrderTime = new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero),
                TotalPrice = 40m
            }
        ]);

        var scopedOrders = db.IntegrationOffsetOrders.Where(o => o.OrderNo.StartsWith("O-"));

        var daily = await scopedOrders
            .GroupBy(o => o.OrderTime.Date)
            .Select(g => new IntegrationSummaryLineChartOffsetRow
            {
                Date = g.Key,
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var monthly = await scopedOrders
            .GroupBy(o => new { o.OrderTime.Year, o.OrderTime.Month })
            .Select(g => new IntegrationSummaryLineChartOffsetRow
            {
                Date = new DateTimeOffset(g.Key.Year, g.Key.Month, 1, 0, 0, 0, TimeSpan.Zero),
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var quarterly = await scopedOrders
            .GroupBy(o => new { o.OrderTime.Year, Quarter = (o.OrderTime.Month - 1) / 3 + 1 })
            .Select(g => new IntegrationSummaryLineChartOffsetRow
            {
                Date = new DateTimeOffset(g.Key.Year, (g.Key.Quarter - 1) * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero),
                Count = g.LongCount(),
                TotalAmount = g.Sum(o => o.TotalPrice)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        Assert.Contains(daily, row => row.Date == new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero) && row.Count == 2 && row.TotalAmount == 25.5m);
        Assert.All(daily, row => Assert.Equal(TimeSpan.Zero, row.Date.Offset));
        Assert.Contains(monthly, row => row.Date == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) && row.Count == 2 && row.TotalAmount == 25.5m);
        Assert.Contains(monthly, row => row.Date == new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) && row.Count == 1 && row.TotalAmount == 20m);
        Assert.All(monthly, row => Assert.Equal(TimeSpan.Zero, row.Date.Offset));
        Assert.Contains(quarterly, row => row.Date == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) && row.Count == 3 && row.TotalAmount == 45.5m);
        Assert.Contains(quarterly, row => row.Date == new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero) && row.Count == 2 && row.TotalAmount == 70m);
        Assert.All(quarterly, row => Assert.Equal(TimeSpan.Zero, row.Date.Offset));
    }
}
