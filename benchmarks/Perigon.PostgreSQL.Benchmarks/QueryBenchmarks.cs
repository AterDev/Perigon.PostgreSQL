using System.Data;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[SimpleJob(warmupCount: 3, iterationCount: 8, launchCount: 1)]
public class QueryBenchmarks
{
    private const string QuerySql = """
        SELECT id AS "Id", user_name AS "UserName", age AS "Age",
               is_active AS "IsActive", created_at AS "CreatedAt"
        FROM benchmark_users
        WHERE is_active = TRUE AND age >= @min_age
        ORDER BY created_at DESC
        LIMIT @limit
        """;

    private NpgsqlDataSource _dataSource = null!;
    private Microsoft.EntityFrameworkCore.DbContextOptions<EfBenchmarkDbContext> _efOptions = null!;

    [ParamsSource(nameof(MinAgeValues))]
    public int MinAge { get; set; }

    [ParamsSource(nameof(LimitValues))]
    public int Limit { get; set; }

    public IEnumerable<int> MinAgeValues => BenchmarkConfiguration.ReadIntValues("PERIGON_BENCHMARK_MIN_AGE", 20, 40);

    public IEnumerable<int> LimitValues => BenchmarkConfiguration.ReadIntValues("PERIGON_BENCHMARK_LIMIT", 1, 100, 10_000);

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var connectionString = BenchmarkConfiguration.BuildConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _efOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<EfBenchmarkDbContext>()
            .UseNpgsql(_dataSource)
            .EnableDetailedErrors(false)
            .EnableSensitiveDataLogging(false)
            .Options;

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await BenchmarkConfiguration.InitializeDatabaseAsync(_dataSource);

        // Warm each provider's model/metadata before BenchmarkDotNet starts timing.
        _ = await PerigonAsync();
        _ = await EfCoreAsync();
        _ = await DapperAsync();
        var expected = await PerigonAsync();
        AssertEquivalent(expected, await EfCoreAsync(), "EF Core");
        AssertEquivalent(expected, await DapperAsync(), "Dapper");
        AssertEquivalent(expected, await NpgsqlAsync(), "Npgsql");
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_dataSource is not null)
        {
            await BenchmarkConfiguration.DropSchemaAsync(_dataSource);
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Benchmark(Baseline = true, Description = "Perigon PostgreSQL")]
    public Task<List<BenchmarkUser>> PerigonAsync()
    {
        return ExecutePerigonAsync();
    }

    [Benchmark(Description = "EF Core AsNoTracking")]
    public async Task<List<BenchmarkUser>> EfCoreAsync()
    {
        await using var db = new EfBenchmarkDbContext(_efOptions);
        var query = db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.Age >= MinAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(Limit);
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query);
    }

    [Benchmark(Description = "Dapper")]
    public async Task<List<BenchmarkUser>> DapperAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<BenchmarkUser>(
            QuerySql,
            new { min_age = MinAge, limit = Limit });
        return rows.AsList();
    }

    [Benchmark(Description = "Npgsql handwritten")]
    public async Task<List<BenchmarkUser>> NpgsqlAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = QuerySql;
        command.Parameters.AddWithValue("min_age", MinAge);
        command.Parameters.AddWithValue("limit", Limit);

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BenchmarkUser>(Limit);
        while (await reader.ReadAsync())
        {
            rows.Add(new BenchmarkUser
            {
                Id = reader.GetInt32(0),
                UserName = reader.GetString(1),
                Age = reader.GetInt32(2),
                IsActive = reader.GetBoolean(3),
                CreatedAt = reader.GetFieldValue<DateTime>(4)
            });
        }

        return rows;
    }

    private async Task<List<BenchmarkUser>> ExecutePerigonAsync()
    {
        await using var db = new PerigonBenchmarkDbContext(_dataSource);
        var query = db.Users
            .Where(user => user.IsActive && user.Age >= MinAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(Limit);
        return await Perigon.PostgreSQL.PostgresQueryableExtensions.ToListAsync(query);
    }

    private static void AssertEquivalent(
        IReadOnlyList<BenchmarkUser> expected,
        IReadOnlyList<BenchmarkUser> actual,
        string provider)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidOperationException($"{provider} returned {actual.Count} rows; expected {expected.Count}.");
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var left = expected[i];
            var right = actual[i];
            if (left.Id != right.Id ||
                left.UserName != right.UserName ||
                left.Age != right.Age ||
                left.IsActive != right.IsActive ||
                left.CreatedAt != right.CreatedAt)
            {
                throw new InvalidOperationException($"{provider} result differs at row {i}.");
            }
        }
    }
}

[MemoryDiagnoser]
[RankColumn]
[SimpleJob(warmupCount: 3, iterationCount: 8, launchCount: 1)]
public class TranslationBenchmarks
{
    private NpgsqlDataSource _dataSource = null!;
    private PerigonBenchmarkDbContext _db = null!;
    private IQueryable<BenchmarkUser> _reusedQuery = null!;
    private IQueryable<BenchmarkUser> _capturedQuery = null!;
    private int _capturedMinAge;

    [ParamsSource(nameof(MinAgeValues))]
    public int MinAge { get; set; }

    [ParamsSource(nameof(LimitValues))]
    public int Limit { get; set; }

    public IEnumerable<int> MinAgeValues => BenchmarkConfiguration.ReadIntValues("PERIGON_BENCHMARK_MIN_AGE", 20, 40);

    public IEnumerable<int> LimitValues => BenchmarkConfiguration.ReadIntValues("PERIGON_BENCHMARK_LIMIT", 1, 100, 10_000);

    [GlobalSetup]
    public void GlobalSetup()
    {
        _dataSource = NpgsqlDataSource.Create(BenchmarkConfiguration.BuildConnectionString());
        _db = new PerigonBenchmarkDbContext(_dataSource);
        _reusedQuery = BuildQuery(MinAge);
        _capturedMinAge = MinAge;
        _capturedQuery = _db.Users
            .Where(user => user.IsActive && user.Age >= _capturedMinAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(Limit);

        // Validate that the benchmark exercises the intended shape and that the
        // captured value is read from the current closure on every translation.
        var initial = _capturedQuery.ToSql();
        _capturedMinAge++;
        var changed = _capturedQuery.ToSql();
        if (initial.CommandText != changed.CommandText ||
            initial.Parameters.Count != changed.Parameters.Count ||
            !initial.Parameters.Zip(changed.Parameters)
                .Any(pair => !Equals(pair.First.Value, pair.Second.Value)))
        {
            throw new InvalidOperationException("Translation benchmark did not observe the changed captured parameter.");
        }

        _capturedMinAge = MinAge;
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    [Benchmark(Baseline = true, Description = "Rebuild IQueryable")]
    public BoundSql RebuildQueryable()
    {
        return BuildQuery(MinAge).ToSql();
    }

    [Benchmark(Description = "Reuse IQueryable")]
    public BoundSql ReuseQueryable()
    {
        return _reusedQuery.ToSql();
    }

    [Benchmark(Description = "Reuse IQueryable, changing capture")]
    public BoundSql ReuseQueryableChangingCapturedParameter()
    {
        _capturedMinAge = _capturedMinAge == MinAge ? MinAge + 1 : MinAge;
        return _capturedQuery.ToSql();
    }

    private IQueryable<BenchmarkUser> BuildQuery(int minAge)
    {
        return _db.Users
            .Where(user => user.IsActive && user.Age >= minAge)
            .OrderByDescending(user => user.CreatedAt)
            .Take(Limit);
    }
}

[Table("benchmark_users")]
public sealed class BenchmarkUser
{
    [Column("id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("user_name")]
    public string UserName { get; set; } = "";

    public int Age { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

public sealed class PerigonBenchmarkDbContext : Perigon.PostgreSQL.DbContext
{
    public PerigonBenchmarkDbContext(NpgsqlDataSource dataSource)
        : base(builder => builder.UseNpgsql(dataSource))
    {
    }

    public Perigon.PostgreSQL.DbSet<BenchmarkUser> Users => Set<BenchmarkUser>();
}

public sealed class EfBenchmarkDbContext : Microsoft.EntityFrameworkCore.DbContext
{
    public EfBenchmarkDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<EfBenchmarkDbContext> options)
        : base(options)
    {
    }

    public Microsoft.EntityFrameworkCore.DbSet<BenchmarkUser> Users => Set<BenchmarkUser>();

    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BenchmarkUser>();
        entity.ToTable("benchmark_users");
        entity.HasKey(user => user.Id);
        entity.Property(user => user.Id).HasColumnName("id");
        entity.Property(user => user.UserName).HasColumnName("user_name");
        entity.Property(user => user.Age).HasColumnName("age");
        entity.Property(user => user.IsActive).HasColumnName("is_active");
        entity.Property(user => user.CreatedAt).HasColumnName("created_at");
    }
}

internal static class BenchmarkConfiguration
{
    public static readonly string SchemaName = CreateSchemaName();

    public static IReadOnlyList<int> ReadIntValues(string variableName, params int[] defaults)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaults;
        }

        var values = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new InvalidOperationException($"{variableName} must contain positive comma-separated integers."))
            .Distinct()
            .ToArray();
        return values.Length > 0
            ? values
            : throw new InvalidOperationException($"{variableName} must contain at least one value.");
    }

    public static string BuildConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("PERIGON_BENCHMARK_POSTGRES_CONNECTION_STRING")
            ?? "Host=127.0.0.1;Port=5432;Database=perigon_benchmark;Username=postgres;Password=postgres";
        var builder = new NpgsqlConnectionStringBuilder(configured)
        {
            Pooling = true,
            MinPoolSize = 1,
            MaxPoolSize = 64,
            SearchPath = SchemaName
        };
        return builder.ConnectionString;
    }

    public static async Task InitializeDatabaseAsync(NpgsqlDataSource dataSource)
    {
        var schema = SchemaName;

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{QuoteIdentifier(schema)}";
            CREATE TABLE "{QuoteIdentifier(schema)}".benchmark_users (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                user_name text NOT NULL,
                age integer NOT NULL,
                is_active boolean NOT NULL,
                created_at timestamp with time zone NOT NULL
            );
            CREATE INDEX benchmark_users_query_idx
                ON "{QuoteIdentifier(schema)}".benchmark_users (is_active, created_at DESC);
            INSERT INTO "{QuoteIdentifier(schema)}".benchmark_users (user_name, age, is_active, created_at)
            SELECT 'user-' || lpad(g::text, 7, '0'),
                   18 + (g % 63),
                   g % 5 <> 0,
                   now() - (g || ' minutes')::interval
            FROM generate_series(1, 100000) AS g;
            ANALYZE "{QuoteIdentifier(schema)}".benchmark_users;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropSchemaAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{QuoteIdentifier(SchemaName)}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateSchemaName()
    {
        var prefix = Environment.GetEnvironmentVariable("PERIGON_BENCHMARK_SCHEMA") ?? "perigon_benchmark";
        prefix = new string(prefix.Where(static c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "perigon_benchmark";
        }

        var candidate = $"{prefix[..Math.Min(prefix.Length, 45)]}_{Guid.NewGuid():N}";
        return candidate[..Math.Min(candidate.Length, 63)];
    }

    private static string QuoteIdentifier(string identifier) => identifier.Replace("\"", "\"\"");
}
