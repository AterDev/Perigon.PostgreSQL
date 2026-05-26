using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TransactionIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public TransactionIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Transaction_commits_changes()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);

        await db.TransactionAsync(async ct =>
        {
            _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
            {
                UserName = "Tx-Commit",
                Age = 30,
                IsActive = true,
                Status = "tx-commit",
                CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["tx"],
                ProfileJson = """{"tx":"commit"}"""
            }, ct);
        });

        Assert.True(await db.IntegrationUsers.Where(u => u.UserName == "Tx-Commit").AnyAsync());
    }

    [Fact]
    public async Task Transaction_rolls_back_on_exception()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.TransactionAsync(async ct =>
            {
                _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
                {
                    UserName = "Tx-Rollback",
                    Age = 30,
                    IsActive = true,
                    Status = "tx-rollback",
                    CreatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                    Tags = ["tx"],
                    ProfileJson = """{"tx":"rollback"}"""
                }, ct);
                throw new InvalidOperationException("rollback");
            }));

        Assert.False(await db.IntegrationUsers.Where(u => u.UserName == "Tx-Rollback").AnyAsync());
    }

    [Fact]
    public async Task Transaction_rolls_back_all_changes_on_database_error()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.TransactionAsync(async ct =>
            {
                _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
                {
                    UserName = "Tx-DbError",
                    Age = 31,
                    IsActive = true,
                    Status = "tx-db-error",
                    CreatedAt = new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc),
                    Tags = ["tx", "db-error"],
                    ProfileJson = """{"tx":"db-error-1"}"""
                }, ct);

                _ = await db.IntegrationUsers.InsertAsync(new IntegrationUser
                {
                    UserName = "Tx-DbError",
                    Age = 32,
                    IsActive = false,
                    Status = "tx-db-error",
                    CreatedAt = new DateTime(2026, 2, 6, 0, 0, 0, DateTimeKind.Utc),
                    Tags = ["tx", "db-error"],
                    ProfileJson = """{"tx":"db-error-2"}"""
                }, ct);
            }));

        Assert.False(await db.IntegrationUsers.Where(u => u.Status == "tx-db-error").AnyAsync());
    }

    [Fact]
    public async Task Nested_transaction_is_rejected()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.TransactionAsync(_ => db.TransactionAsync(_ => Task.CompletedTask)));
    }

    [Fact]
    public async Task Transaction_can_wrap_copy_bulk_insert()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var rows = new[]
        {
            new IntegrationUser
            {
                UserName = "Tx-Copy-1",
                Age = 21,
                IsActive = true,
                Status = "tx-copy",
                CreatedAt = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["tx", "copy"],
                ProfileJson = """{"tx":"copy"}"""
            },
            new IntegrationUser
            {
                UserName = "Tx-Copy-2",
                Age = 22,
                IsActive = true,
                Status = "tx-copy",
                CreatedAt = new DateTime(2026, 2, 4, 0, 0, 0, DateTimeKind.Utc),
                Tags = ["tx", "copy"],
                ProfileJson = """{"tx":"copy"}"""
            }
        };

        await db.TransactionAsync(ct => db.IntegrationUsers.BulkInsertAsync(rows, cancellationToken: ct));

        Assert.True(await db.IntegrationUsers.Where(u => u.Status == "tx-copy").CountAsync() >= 2);
    }

    [Fact]
    public async Task Transaction_can_commit_multiple_write_operations_atomically()
    {
        await using var db = new IntegrationDbContext(_fixture.ConnectionString);
        var keep = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Tx-Atomic-Keep",
            Age = 26,
            IsActive = true,
            Status = "tx-atomic-before",
            CreatedAt = new DateTime(2026, 2, 7, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["tx", "atomic"],
            ProfileJson = """{"tx":"keep"}"""
        });

        var remove = await db.IntegrationUsers.InsertAsync(new IntegrationUser
        {
            UserName = "Tx-Atomic-Remove",
            Age = 27,
            IsActive = true,
            Status = "tx-atomic-before",
            CreatedAt = new DateTime(2026, 2, 8, 0, 0, 0, DateTimeKind.Utc),
            Tags = ["tx", "atomic"],
            ProfileJson = """{"tx":"remove"}"""
        });

        await db.TransactionAsync(async ct =>
        {
            _ = await db.IntegrationUsers
                .Where(u => u.Id == keep.Id)
                .ExecuteUpdateAsync(s => s.Set(u => u.Status, "tx-atomic-after"), options: null, cancellationToken: ct);

            _ = await db.IntegrationUsers
                .Where(u => u.Id == remove.Id)
                .ExecuteDeleteAsync(options: null, cancellationToken: ct);
        });

        var kept = await db.IntegrationUsers.Where(u => u.Id == keep.Id).ToListAsync();
        var deleted = await db.IntegrationUsers.Where(u => u.Id == remove.Id).ToListAsync();

        Assert.Equal("tx-atomic-after", kept.Single().Status);
        Assert.Empty(deleted);
    }
}
