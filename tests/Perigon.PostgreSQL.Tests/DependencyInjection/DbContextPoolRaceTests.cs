using System.Reflection;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL.Tests.DependencyInjection;

public sealed class DbContextPoolRaceTests
{
    [Fact]
    public async Task Dispose_racing_with_factory_creation_does_not_return_a_context_from_a_disposed_pool()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RacePoolContext? created = null;
        var pool = new DbContextPool<RacePoolContext>(() =>
        {
            factoryStarted.SetResult();
            releaseFactory.Task.GetAwaiter().GetResult();
            created = new RacePoolContext(new DbContextOptions<RacePoolContext>());
            return created;
        }, capacity: 1);

        var rentTask = Task.Run(() => Record.Exception(() => pool.Rent()));

        try
        {
            await factoryStarted.Task;
            pool.Dispose();
            releaseFactory.SetResult();

            var exception = await rentTask;
            Assert.IsType<ObjectDisposedException>(exception);
        }
        finally
        {
            releaseFactory.TrySetResult();
            pool.Dispose();
            created?.Dispose();
        }
    }

    [Fact]
    public async Task Transaction_execution_context_from_an_old_lease_cannot_reenter_the_current_lease()
    {
        var pool = new DbContextPool<TransactionPoolContext>(
            () => new TransactionPoolContext(new DbContextOptions<TransactionPoolContext>()),
            capacity: 1);
        var first = pool.Rent();
        var oldTransactionOperation = EnterTransactionOperation(first);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var child = Task.Run(async () =>
        {
            childStarted.SetResult();
            await continueChild.Task.ConfigureAwait(false);

            try
            {
                using var operation = first.EnterOperation();
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        try
        {
            await childStarted.Task;
            oldTransactionOperation.Dispose();
            pool.Return(first);

            var current = pool.Rent();
            Assert.Same(first, current);
            var currentOperation = current.EnterOperation();
            try
            {
                continueChild.SetResult();
                var exception = await child;
                Assert.IsType<ObjectDisposedException>(exception);
            }
            finally
            {
                currentOperation.Dispose();
                pool.Return(current);
            }
        }
        finally
        {
            oldTransactionOperation.Dispose();
            continueChild.TrySetResult();
            pool.Dispose();
        }
    }

    [Fact]
    public async Task Ignored_filter_execution_context_from_an_old_lease_cannot_disable_current_filters()
    {
        var pool = new DbContextPool<FilterPoolContext>(
            () => new FilterPoolContext(new DbContextOptions<FilterPoolContext>()),
            capacity: 1);
        var first = pool.Rent();
        var ignoredFilterScope = ((IQueryFilterContext)first).PushIgnoredFilters(
            new HashSet<string>(["SoftDelete"], StringComparer.Ordinal));
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var child = Task.Run(async () =>
        {
            childStarted.SetResult();
            await continueChild.Task.ConfigureAwait(false);

            try
            {
                var sql = first.Rows.ToQuerySql();
                return (Exception: (Exception?)null, CommandText: sql.CommandText);
            }
            catch (Exception exception)
            {
                return (Exception: exception, CommandText: (string?)null);
            }
        });

        try
        {
            await childStarted.Task;
            ignoredFilterScope.Dispose();
            pool.Return(first);

            var current = pool.Rent();
            Assert.Same(first, current);
            continueChild.SetResult();
            var result = await child;

            Assert.IsType<ObjectDisposedException>(result.Exception);

            pool.Return(current);
        }
        finally
        {
            ignoredFilterScope.Dispose();
            continueChild.TrySetResult();
            pool.Dispose();
        }
    }

    private static IDisposable EnterTransactionOperation(DbContext context)
    {
        var method = typeof(DbContext).GetMethod(
            "EnterTransactionOperation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (IDisposable)(method!.Invoke(context, null)
            ?? throw new InvalidOperationException("The transaction operation lease was not created."));
    }

    private sealed class RacePoolContext(DbContextOptions<RacePoolContext> options)
        : DbContext(options), IPooledDbContext
    {
        public void ResetState() { }
    }

    private sealed class TransactionPoolContext(DbContextOptions<TransactionPoolContext> options)
        : DbContext(options), IPooledDbContext
    {
        public void ResetState() { }
    }

    private sealed class FilterPoolContext(DbContextOptions<FilterPoolContext> options)
        : DbContext(options), IPooledDbContext
    {
        public DbSet<FilterPoolRow> Rows => Set<FilterPoolRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => HasQueryFilter<FilterPoolRow>("SoftDelete", row => !row.IsDeleted);

        public void ResetState() { }
    }

    private sealed class FilterPoolRow
    {
        public int Id { get; set; }

        public bool IsDeleted { get; set; }
    }
}
