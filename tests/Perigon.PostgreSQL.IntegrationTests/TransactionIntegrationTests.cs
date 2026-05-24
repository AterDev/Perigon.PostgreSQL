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
}
