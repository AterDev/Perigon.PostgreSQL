using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Options;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class DbContextPoolIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Concurrent_scopes_do_not_leak_tenant_state_or_rows()
    {
        var schema = fixture.CreateIsolatedSchema("pool_parallel_" + Guid.NewGuid().ToString("N")[..8]);
        var connectionString = WithSearchPath(fixture.ConnectionString, schema);
        var services = new ServiceCollection();
        services.AddDbContextPool<PooledIntegrationDbContext>(options => options.UseNpgsql(connectionString), poolSize: 4);
        await using var provider = services.BuildServiceProvider();
        using (var setup = provider.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            await db.EnsureCreatedAsync();
            for (var tenant = 1; tenant <= 8; tenant++)
                await db.Rows.InsertAsync(new PooledIntegrationRow { TenantId = tenant, Name = $"tenant-{tenant}" });
        }

        var tasks = Enumerable.Range(0, 128).Select(async i =>
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            Assert.Equal(0, db.TenantId);
            var expectedTenant = i % 8 + 1;
            db.TenantId = expectedTenant;
            var rows = await db.Rows.ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(expectedTenant, row.TenantId);
            Assert.Equal($"tenant-{expectedTenant}", row.Name);
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Consecutive_scopes_reuse_context_reset_tenant_state_and_isolate_filtered_rows()
    {
        var schema = fixture.CreateIsolatedSchema("pool_tenant_" + Guid.NewGuid().ToString("N")[..8]);
        var connectionString = WithSearchPath(fixture.ConnectionString, schema);
        var services = new ServiceCollection();
        services.AddDbContextPool<PooledIntegrationDbContext>(
            options => options.UseNpgsql(connectionString),
            poolSize: 1);

        await using var provider = services.BuildServiceProvider();
        PooledIntegrationDbContext first;
        DbSet<PooledIntegrationRow> firstSet;
        IQueryable<PooledIntegrationRow> oldQuery;

        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            await first.EnsureCreatedAsync();

            first.TenantId = 7;
            firstSet = first.Rows;
            var tenantSeven = await first.Rows.InsertAsync(new PooledIntegrationRow
            {
                TenantId = 7,
                Name = "tenant-seven"
            });
            _ = await first.Rows.InsertAsync(new PooledIntegrationRow
            {
                TenantId = 9,
                Name = "tenant-nine"
            });

            IQueryable<PooledIntegrationRow> unfilteredQuery;
            using (((IQueryFilterContext)first).PushIgnoredFilters(new HashSet<string> { "Tenant" }))
            {
                unfilteredQuery = first.Rows.Where(row => row.Name.StartsWith("tenant-"));
                Assert.Equal(2, (await unfilteredQuery.ToListAsync()).Count);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => unfilteredQuery.ToListAsync());

            oldQuery = firstSet.Where(row => row.Id == tenantSeven.Id);
            var visible = await oldQuery.ToListAsync();
            Assert.Equal(["tenant-seven"], visible.Select(row => row.Name));
            Assert.Same(firstSet, first.Rows);
        }

        using (var scope = provider.CreateScope())
        {
            var reused = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            Assert.Same(first, reused);
            Assert.Equal(0, reused.TenantId);
            Assert.Equal(1, reused.ModelCreatingCalls);

            reused.TenantId = 9;
            Assert.NotSame(firstSet, reused.Rows);
            var visible = await reused.Rows.OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(["tenant-nine"], visible.Select(row => row.Name));

            Assert.Throws<ObjectDisposedException>(() => oldQuery.ToQuerySql());
        }
    }

    [Fact]
    public async Task Committed_and_rolled_back_transactions_leave_reused_context_usable_and_invalidate_old_queries()
    {
        var schema = fixture.CreateIsolatedSchema("pool_transaction_" + Guid.NewGuid().ToString("N")[..8]);
        var connectionString = WithSearchPath(fixture.ConnectionString, schema);
        var services = new ServiceCollection();
        services.AddDbContextPool<PooledIntegrationDbContext>(
            options => options.UseNpgsql(connectionString),
            poolSize: 1);

        await using var provider = services.BuildServiceProvider();
        PooledIntegrationDbContext first;
        IQueryable<PooledIntegrationRow> oldQuery;
        const int tenantId = 11;

        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            await first.EnsureCreatedAsync();
            first.TenantId = tenantId;

            const string committedName = "transaction-committed";
            await first.TransactionAsync(ct => first.Rows.InsertAsync(new PooledIntegrationRow
            {
                TenantId = tenantId,
                Name = committedName
            }, ct));

            Assert.True(await first.Rows.Where(row => row.Name == committedName).AnyAsync());
            oldQuery = first.Rows.Where(row => row.Name == committedName);
        }

        using (var scope = provider.CreateScope())
        {
            var reused = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            Assert.Same(first, reused);
            Assert.Equal(0, reused.TenantId);
            Assert.Throws<ObjectDisposedException>(() => oldQuery.ToQuerySql());

            reused.TenantId = tenantId;
            const string rolledBackName = "transaction-rolled-back";
            await Assert.ThrowsAsync<InvalidOperationException>(() => reused.TransactionAsync(async ct =>
            {
                _ = await reused.Rows.InsertAsync(new PooledIntegrationRow
                {
                    TenantId = tenantId,
                    Name = rolledBackName
                }, ct);
                throw new InvalidOperationException("rollback");
            }));

            Assert.True(await reused.Rows.Where(row => row.Name == "transaction-committed").AnyAsync());
            Assert.False(await reused.Rows.Where(row => row.Name == rolledBackName).AnyAsync());
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reused.TransactionAsync(async ct =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, cancellation.Token));
        }

        using (var scope = provider.CreateScope())
        {
            var afterRollback = scope.ServiceProvider.GetRequiredService<PooledIntegrationDbContext>();
            Assert.Same(first, afterRollback);
            Assert.Equal(0, afterRollback.TenantId);

            afterRollback.TenantId = tenantId;
            var rows = await afterRollback.Rows.OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(["transaction-committed"], rows.Select(row => row.Name));
        }
    }

    private static string WithSearchPath(string connectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schema
        };
        return builder.ConnectionString;
    }
}

public sealed class PooledIntegrationDbContext : DbContext
{
    private readonly string _schema;

    public PooledIntegrationDbContext(DbContextOptions<PooledIntegrationDbContext> options)
        : base(options)
    {
        _schema = new NpgsqlConnectionStringBuilder(options.ConnectionString).SearchPath
            ?? throw new InvalidOperationException("The pooled integration context requires a schema search path.");
    }

    public int TenantId { get; set; }

    public int ModelCreatingCalls { get; private set; }

    public DbSet<PooledIntegrationRow> Rows => Set<PooledIntegrationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ModelCreatingCalls++;
        modelBuilder.Entity<PooledIntegrationRow>().ToTable("pooled_integration_rows", _schema);
        HasQueryFilter<PooledIntegrationRow>("Tenant", row => row.TenantId == TenantId);
    }

    protected override void OnPoolReturn()
    {
        TenantId = 0;
    }
}

public sealed class PooledIntegrationRow
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public string Name { get; set; } = "";
}
