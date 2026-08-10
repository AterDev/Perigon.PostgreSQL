using Perigon.PostgreSQL.Bulk;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class BulkInsertSqlTests
{
    [Fact]
    public async Task Copy_mode_rejects_non_default_batch_size_instead_of_ignoring_it()
    {
        using var db = new TestDbContext();

        await Assert.ThrowsAsync<ArgumentException>(() => db.RichUsers.BulkInsertAsync(
            [new RichUser { UserName = "copy" }],
            new BulkInsertOptions { Mode = BulkInsertMode.Copy, BatchSize = 1 }));
    }

    [Fact]
    public async Task Bulk_insert_honors_pre_cancelled_token_before_opening_a_connection()
    {
        using var db = new TestDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.RichUsers.BulkInsertAsync(
            [new RichUser { UserName = "cancelled" }],
            new BulkInsertOptions(),
            cancellation.Token));
    }
}
