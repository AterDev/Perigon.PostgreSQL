using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Migrations;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MigrationAttribute : Attribute
{
    public MigrationAttribute(Type contextType, string id)
    {
        ContextType = contextType ?? throw new ArgumentNullException(nameof(contextType));
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Migration ID cannot be empty.", nameof(id)) : id;
    }

    public Type ContextType { get; }

    public string Id { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MigrationSnapshotAttribute : Attribute
{
    public MigrationSnapshotAttribute(Type contextType)
    {
        ContextType = contextType ?? throw new ArgumentNullException(nameof(contextType));
    }

    public Type ContextType { get; }
}

public abstract class Migration
{
    public abstract void Up(MigrationBuilder migrationBuilder);

    public abstract void Down(MigrationBuilder migrationBuilder);

    public IReadOnlyList<MigrationCommand> GetUpCommands() => BuildCommands(Up);

    public IReadOnlyList<MigrationCommand> GetDownCommands() => BuildCommands(Down);

    public void ValidateReversible()
    {
        var up = GetUpCommands();
        var down = GetDownCommands();
        if (up.Count > 0 && down.Count == 0)
        {
            throw new InvalidOperationException(
                $"Migration '{GetType().FullName}' has Up operations but no Down operations. Provide a reverse operation for every migration.");
        }
    }

    private IReadOnlyList<MigrationCommand> BuildCommands(Action<MigrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new MigrationBuilder();
        configure(builder);
        return builder.Build();
    }
}

public sealed class MigrationBuilder
{
    private readonly List<MigrationCommand> _commands = [];

    public void Sql(string sql, bool isDestructive = false, string? warning = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Migration SQL cannot be empty.", nameof(sql));
        }

        _commands.Add(new MigrationCommand(sql.Trim(), isDestructive, warning));
    }

    public void RenameColumn(string? schema, string table, string oldName, string newName)
    {
        var store = Identifier.Qualify(schema, table);
        Sql($"ALTER TABLE {store} RENAME COLUMN {Identifier.Quote(oldName)} TO {Identifier.Quote(newName)}");
    }

    public void RenameTable(string? schema, string oldName, string newName)
    {
        Sql($"ALTER TABLE {Identifier.Qualify(schema, oldName)} RENAME TO {Identifier.Quote(newName)}");
    }

    internal IReadOnlyList<MigrationCommand> Build() => _commands.ToArray();
}

public sealed record MigrationCommand(string Sql, bool IsDestructive = false, string? Warning = null);

public sealed class RegisteredMigration
{
    public RegisteredMigration(string id, Migration instance)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Migration ID cannot be empty.", nameof(id)) : id;
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    public string Id { get; }

    public Migration Instance { get; }
}

public static class MigrationRegistry
{
    private static readonly Dictionary<Type, IReadOnlyList<RegisteredMigration>> ContextMigrations = new();
    private static readonly object Sync = new();

    public static void RegisterContext<TContext>(IReadOnlyList<RegisteredMigration> migrations)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(migrations);
        lock (Sync)
        {
            var existing = ContextMigrations.TryGetValue(typeof(TContext), out var current)
                ? current
                : Array.Empty<RegisteredMigration>();
            var combined = existing.Concat(migrations)
                .OrderBy(static migration => migration.Id, StringComparer.Ordinal)
                .ToArray();
            var duplicate = combined.GroupBy(static migration => migration.Id, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Migration ID '{duplicate.Key}' is registered more than once for DbContext '{typeof(TContext).FullName}'.");
            }

            ContextMigrations[typeof(TContext)] = combined;
        }
    }

    public static IReadOnlyList<RegisteredMigration> GetContextMigrations(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        lock (Sync)
        {
            return ContextMigrations.TryGetValue(contextType, out var migrations)
                ? migrations
                : Array.Empty<RegisteredMigration>();
        }
    }

    public static IReadOnlyList<RegisteredMigration> GetContextMigrations<TContext>()
        where TContext : DbContext => GetContextMigrations(typeof(TContext));
}

public abstract class MigrationModelSnapshot
{
    public abstract MigrationModel Model { get; }
}
