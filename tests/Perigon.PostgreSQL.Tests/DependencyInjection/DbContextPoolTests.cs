using Microsoft.Extensions.DependencyInjection;
using Perigon.PostgreSQL.Options;
using Perigon.PostgreSQL.RawSql;

namespace Perigon.PostgreSQL.Tests.DependencyInjection;

public sealed class DbContextPoolTests
{
    [Fact]
    public void Pool_reuses_context_but_invalidates_old_sets_queries_and_raw_sql()
    {
        var services = new ServiceCollection();
        var configCalls = 0;
        services.AddDbContextPool<PoolContext>(options =>
        {
            configCalls++;
            options.UseNpgsql("Host=localhost;Database=perigon_tests;Username=postgres");
        }, poolSize: 1);
        using var provider = services.BuildServiceProvider();
        PoolContext first;
        DbSet<PoolRow> oldSet;
        IQueryable<PoolRow> oldQuery;
        RawSqlCommand oldCommand;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<PoolContext>();
            first.TenantId = 7;
            oldSet = first.Rows;
            Assert.Same(oldSet, first.Rows);
            oldQuery = oldSet.Where(row => row.Id > 0);
            oldCommand = first.SqlCommand($"SELECT {7}");
            Assert.Contains(oldQuery.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 7));
        }

        using var nextScope = provider.CreateScope();
        var next = nextScope.ServiceProvider.GetRequiredService<PoolContext>();
        Assert.Same(first, next);
        Assert.Equal(0, next.TenantId);
        Assert.Equal(1, next.ModelCreatingCalls);
        Assert.Equal(1, configCalls);
        next.TenantId = 9;
        Assert.NotSame(oldSet, next.Rows);
        Assert.Contains(next.Rows.ToQuerySql().Parameters, parameter => Equals(parameter.Value, 9));
        Assert.Throws<ObjectDisposedException>(() => oldSet.ToQuerySql());
        Assert.Throws<ObjectDisposedException>(() => oldQuery.ToQuerySql());
        Assert.Throws<ObjectDisposedException>(() => oldCommand.ToBoundSql());
    }

    [Fact]
    public void Default_pool_return_hook_does_not_reset_application_state_automatically()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<DefaultHookPoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();

        DefaultHookPoolContext first;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<DefaultHookPoolContext>();
            first.TenantId = 42;
        }

        using var nextScope = provider.CreateScope();
        var reused = nextScope.ServiceProvider.GetRequiredService<DefaultHookPoolContext>();
        Assert.Same(first, reused);
        Assert.Equal(42, reused.TenantId);
    }

    [Fact]
    public void Manually_disposed_context_is_not_reused_before_its_scope_ends()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<PoolContext>();
        first.Dispose();
        using var nextScope = provider.CreateScope();
        var second = nextScope.ServiceProvider.GetRequiredService<PoolContext>();
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Async_scope_returns_context_and_overflow_does_not_share_active_instances()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(_ => { }, 1);
        await using var provider = services.BuildServiceProvider();
        PoolContext first;
        await using (var scope = provider.CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<PoolContext>();
            await using var parallel = provider.CreateAsyncScope();
            Assert.NotSame(first, parallel.ServiceProvider.GetRequiredService<PoolContext>());
        }
        await using var next = provider.CreateAsyncScope();
        var rented = next.ServiceProvider.GetRequiredService<PoolContext>();
        Assert.NotNull(rented.Rows);
    }

    [Fact]
    public void Context_with_active_operation_is_not_returned_to_pool()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<PoolContext>();
        var operation = first.EnterOperation();
        scope.Dispose();
        using var next = provider.CreateScope();
        Assert.NotSame(first, next.ServiceProvider.GetRequiredService<PoolContext>());
        operation.Dispose();
    }

    [Fact]
    public void Legacy_pool_reset_interface_is_still_invoked()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<LegacyPoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();

        LegacyPoolContext first;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<LegacyPoolContext>();
            first.TenantId = 42;
        }

        using var nextScope = provider.CreateScope();
        var reused = nextScope.ServiceProvider.GetRequiredService<LegacyPoolContext>();
        Assert.Same(first, reused);
        Assert.Equal(0, reused.TenantId);
        Assert.Equal(1, reused.ResetCalls);
    }

    [Fact]
    public void New_and_legacy_hooks_both_run_in_documented_order()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<DualHookContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        DualHookContext first;
        using (var scope = provider.CreateScope())
            first = scope.ServiceProvider.GetRequiredService<DualHookContext>();
        using var next = provider.CreateScope();
        var reused = next.ServiceProvider.GetRequiredService<DualHookContext>();
        Assert.Same(first, reused);
        Assert.Equal(["OnPoolReturn", "ResetState"], reused.Calls);
    }

    [Fact]
    public void Concurrent_scopes_never_share_an_active_context()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(_ => { }, 4);
        using var provider = services.BuildServiceProvider();
        var scopes = new IServiceScope[16];
        var contexts = new PoolContext[scopes.Length];
        try
        {
            Parallel.For(0, scopes.Length, i =>
            {
                scopes[i] = provider.CreateScope();
                contexts[i] = scopes[i].ServiceProvider.GetRequiredService<PoolContext>();
            });
            Assert.Equal(contexts.Length, contexts.Distinct(ReferenceEqualityComparer.Instance).Count());
        }
        finally
        {
            Parallel.ForEach(scopes, scope => scope?.Dispose());
        }
    }

    [Fact]
    public void Pool_rejects_invalid_size_and_contexts_with_additional_dependencies()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddDbContextPool<PoolContext>(_ => { }, 0));
        Assert.Throws<InvalidOperationException>(() => services.AddDbContextPool<InvalidPoolContext>(_ => { }));
    }

    [Fact]
    public void Failed_pool_return_hook_discards_the_context()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        var firstScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<PoolContext>();
        first.FailOnPoolReturn = true;
        Assert.Throws<InvalidOperationException>(firstScope.Dispose);
        using var nextScope = provider.CreateScope();
        Assert.NotSame(first, nextScope.ServiceProvider.GetRequiredService<PoolContext>());
    }

    [Fact]
    public void Pool_preserves_external_data_source_ownership()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=perigon_tests;Username=postgres");
        var services = new ServiceCollection();
        services.AddDbContextPool<PoolContext>(options => options.UseNpgsql(dataSource), 1);
        using (var provider = services.BuildServiceProvider())
        using (var scope = provider.CreateScope())
            Assert.Same(dataSource, scope.ServiceProvider.GetRequiredService<PoolContext>().GetDataSource());
        using var command = dataSource.CreateCommand("SELECT 1");
        Assert.NotNull(command);
    }

    [Fact]
    public void Eagerly_created_dbsets_are_rejected_for_pooling()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<EagerPoolContext>(_ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<EagerPoolContext>());
    }

    [Fact]
    public void Pooled_filters_reject_captured_tenant_snapshots_and_discard_failed_models()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<CapturedTenantContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        CapturedTenantContext first;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<CapturedTenantContext>();
            first.TenantId = 7;
            Assert.Throws<InvalidOperationException>(() => first.Rows);
        }
        using var next = provider.CreateScope();
        Assert.NotSame(first, next.ServiceProvider.GetRequiredService<CapturedTenantContext>());
    }

    [Fact]
    public void Application_owned_lazy_dbsets_can_be_cleared_by_pool_return_hook()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<LazyPoolContext>(_ => { }, 1);
        using var provider = services.BuildServiceProvider();
        LazyPoolContext first;
        DbSet<PoolRow> oldSet;
        using (var scope = provider.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<LazyPoolContext>();
            oldSet = first.Rows;
        }
        using var next = provider.CreateScope();
        var reused = next.ServiceProvider.GetRequiredService<LazyPoolContext>();
        Assert.Same(first, reused);
        Assert.NotSame(oldSet, reused.Rows);
        Assert.Throws<ObjectDisposedException>(() => oldSet.ToQuerySql());
    }

    public sealed class PoolContext(DbContextOptions<PoolContext> options) : DbContext(options)
    {
        public int TenantId { get; set; }
        public bool FailOnPoolReturn { get; set; }
        public int ModelCreatingCalls { get; private set; }
        public DbSet<PoolRow> Rows => Set<PoolRow>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            ModelCreatingCalls++;
            HasQueryFilter<PoolRow>("Tenant", row => row.TenantId == TenantId);
        }
        protected override void OnPoolReturn()
        {
            if (FailOnPoolReturn) throw new InvalidOperationException("Pool return failed.");
            TenantId = 0;
        }
    }

    public sealed class DefaultHookPoolContext(DbContextOptions<DefaultHookPoolContext> options) : DbContext(options)
    {
        public int TenantId { get; set; }
    }

    public sealed class LegacyPoolContext(DbContextOptions<LegacyPoolContext> options)
        : DbContext(options), IPooledDbContext
    {
        public int TenantId { get; set; }

        public int ResetCalls { get; private set; }

        public void ResetState()
        {
            ResetCalls++;
            TenantId = 0;
        }
    }

    public sealed class DualHookContext(DbContextOptions<DualHookContext> options)
        : DbContext(options), IPooledDbContext
    {
        public List<string> Calls { get; } = [];
        protected override void OnPoolReturn() => Calls.Add("OnPoolReturn");
        public void ResetState() => Calls.Add("ResetState");
    }

    public sealed class EagerPoolContext : DbContext
    {
        public EagerPoolContext(DbContextOptions<EagerPoolContext> options) : base(options) => Rows = Set<PoolRow>();
        public DbSet<PoolRow> Rows { get; }
    }

    public sealed class CapturedTenantContext(DbContextOptions<CapturedTenantContext> options)
        : DbContext(options)
    {
        public int TenantId { get; set; }
        public DbSet<PoolRow> Rows => Set<PoolRow>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var snapshot = TenantId;
            HasQueryFilter<PoolRow>("Tenant", row => row.TenantId == snapshot);
        }
        protected override void OnPoolReturn() => TenantId = 0;
    }

    public sealed class LazyPoolContext(DbContextOptions<LazyPoolContext> options)
        : DbContext(options)
    {
        private DbSet<PoolRow>? _rows;
        public DbSet<PoolRow> Rows => _rows ??= Set<PoolRow>();
        protected override void OnPoolReturn() => _rows = null;
    }

    public sealed class InvalidPoolContext : DbContext
    {
        public InvalidPoolContext(DbContextOptions<InvalidPoolContext> options, IServiceProvider provider) : base(options) => _ = provider;
    }

    public sealed class PoolRow
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
    }
}
