using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Migrations;

internal static class MigrationCli
{
    public static bool IsMigrationCommand(string[] args)
    {
        return args.Length >= 2 &&
            ((args[0].Equals("migrations", StringComparison.OrdinalIgnoreCase) &&
              args[1] is "add" or "list" or "script") ||
             (args[0].Equals("database", StringComparison.OrdinalIgnoreCase) &&
              args[1] is "update" or "baseline"));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = args[0].ToLowerInvariant() + " " + args[1].ToLowerInvariant();
            var parsed = CommandArguments.Parse(args.Skip(2).ToArray());
            return command switch
            {
                "migrations add" => await AddAsync(parsed).ConfigureAwait(false),
                "migrations list" => await ListAsync(parsed).ConfigureAwait(false),
                "migrations script" => await ScriptAsync(parsed).ConfigureAwait(false),
                "database update" => await UpdateAsync(parsed).ConfigureAwait(false),
                "database baseline" => await BaselineAsync(parsed).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown migration command '{command}'.")
            };
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException { InnerException: not null } invocation || ex is TypeInitializationException { InnerException: not null } initialization)
            {
                ex = ex switch
                {
                    TargetInvocationException target => target.InnerException!,
                    TypeInitializationException typeInitialization => typeInitialization.InnerException!,
                    _ => ex
                };
            }

            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> AddAsync(CommandArguments args)
    {
        var name = args.Positional.Count == 0
            ? args.Require("name")
            : args.Positional[0];
        var project = args.RequireProject();
        var output = args.Get("output") ?? Path.Combine(Path.GetDirectoryName(project)!, "Migrations");
        var @namespace = args.Get("namespace");
        await using var loaded = await ProjectContext.LoadAsync(project, args).ConfigureAwait(false);
        var migrationNamespace = @namespace ?? loaded.ContextType.Namespace ?? loaded.Assembly.GetName().Name ?? "Migrations";
        var previous = loaded.FindSnapshot() ?? MigrationModel.Empty;
        var current = loaded.GetMigrationModel();
        var diff = MigrationModelDiffer.Diff(previous, current);

        foreach (var warning in diff.Warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var id = timestamp + "_" + SanitizeIdentifier(name);
        var className = SanitizeIdentifier(name) + "_" + timestamp + "Migration";
        var migrationFile = Path.Combine(output, id + ".cs");
        var snapshotFile = Path.Combine(output, loaded.ContextType.Name + "ModelSnapshot.cs");
        if (File.Exists(migrationFile))
        {
            throw new InvalidOperationException($"Migration file '{migrationFile}' already exists.");
        }

        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(migrationFile, MigrationSourceGenerator.GenerateMigration(
            migrationNamespace, loaded.ContextType, id, className, diff)).ConfigureAwait(false);
        await File.WriteAllTextAsync(snapshotFile, MigrationSourceGenerator.GenerateSnapshot(
            migrationNamespace, loaded.ContextType, current)).ConfigureAwait(false);
        Console.WriteLine($"Generated {migrationFile}");
        Console.WriteLine($"Generated {snapshotFile}");
        return 0;
    }

    private static async Task<int> ListAsync(CommandArguments args)
    {
        var project = args.RequireProject();
        await using var loaded = await ProjectContext.LoadAsync(project, args).ConfigureAwait(false);
        var migrations = loaded.GetMigrations();
        if (migrations.Count == 0)
        {
            Console.WriteLine("No migrations found.");
            return 0;
        }

        foreach (var migration in migrations)
        {
            Console.WriteLine(migration.Id);
        }

        return 0;
    }

    private static async Task<int> ScriptAsync(CommandArguments args)
    {
        var project = args.RequireProject();
        await using var loaded = await ProjectContext.LoadAsync(project, args).ConfigureAwait(false);
        var migrations = loaded.GetMigrations();
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException($"No migrations are registered for DbContext '{loaded.ContextType.FullName}'.");
        }

        var from = args.Get("from") ?? "0";
        var to = args.Get("to") ?? migrations[^1].Id;
        var fromIndex = from == "0" ? -1 : FindMigration(migrations, from);
        var toIndex = to == "0" ? -1 : FindMigration(migrations, to);
        var sql = new StringBuilder();
        var historySchema = args.Get("history-schema") ?? "public";
        var historyTable = args.Get("history-table") ?? "_PerigonMigrationsHistory";
        var historyStore = QuoteIdentifier(historySchema) + "." + QuoteIdentifier(historyTable);
        sql.AppendLine($"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(historySchema)};");
        sql.AppendLine($"CREATE TABLE IF NOT EXISTS {historyStore} (\"MigrationId\" text PRIMARY KEY, \"AppliedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP);");

        if (toIndex >= fromIndex)
        {
            for (var i = fromIndex + 1; i <= toIndex; i++)
            {
                AppendScript(sql, migrations[i], down: false, args.HasFlag("idempotent"), historyStore);
            }
        }
        else
        {
            for (var i = fromIndex; i > toIndex; i--)
            {
                AppendScript(sql, migrations[i], down: true, args.HasFlag("idempotent"), historyStore);
            }
        }

        var output = args.Get("output");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Write(sql.ToString());
        }
        else
        {
            await File.WriteAllTextAsync(output, sql.ToString()).ConfigureAwait(false);
            Console.WriteLine($"Generated {output}");
        }

        return 0;
    }

    private static async Task<int> UpdateAsync(CommandArguments args)
    {
        var project = args.RequireProject();
        var connection = args.Require("connection");
        await using var loaded = await ProjectContext.LoadAsync(project, args, connection).ConfigureAwait(false);
        await loaded.MigrateAsync(args.Get("target"), connection).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> BaselineAsync(CommandArguments args)
    {
        var project = args.RequireProject();
        var connection = args.Require("connection");
        await using var loaded = await ProjectContext.LoadAsync(project, args, connection).ConfigureAwait(false);
        var migrations = loaded.GetMigrations();
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException($"No migrations are registered for DbContext '{loaded.ContextType.FullName}'.");
        }

        var id = args.Get("migration") ?? args.Get("target") ?? migrations[^1].Id;
        var targetIndex = FindMigration(migrations, id);
        if (migrations.Count != 1 || targetIndex != 0)
        {
            throw new InvalidOperationException("Baseline currently requires exactly one registered migration. Generate a baseline migration first; a single model snapshot is available for verification.");
        }
        var snapshot = loaded.FindSnapshot()
            ?? throw new InvalidOperationException("No migration model snapshot was found. Build the project after generating a snapshot.");
        await loaded.BaselineAsync(id, snapshot, connection).ConfigureAwait(false);
        return 0;
    }

    private static void AppendScript(StringBuilder builder, MigrationInfo migration, bool down, bool idempotent, string historyStore)
    {
        var commands = migration.GetCommands(down);
        if (!idempotent)
        {
            builder.AppendLine("BEGIN;");
        }
        else
        {
            builder.AppendLine("DO $$");
            builder.AppendLine("BEGIN");
            builder.AppendLine(down
                ? $"    IF EXISTS (SELECT 1 FROM {historyStore} WHERE \"MigrationId\" = {SqlLiteral(migration.Id)}) THEN"
                : $"    IF NOT EXISTS (SELECT 1 FROM {historyStore} WHERE \"MigrationId\" = {SqlLiteral(migration.Id)}) THEN");
        }

        foreach (var command in commands)
        {
            if (!string.IsNullOrWhiteSpace(command.Warning))
            {
                builder.AppendLine("-- WARNING: " + command.Warning);
            }

            builder.AppendLine(idempotent
                ? "        EXECUTE " + SqlLiteral(command.Sql.TrimEnd(';')) + ";"
                : command.Sql.TrimEnd(';') + ";");
        }

        if (down)
        {
            builder.AppendLine(idempotent
                ? $"        DELETE FROM {historyStore} WHERE \"MigrationId\" = {SqlLiteral(migration.Id)};"
                : $"DELETE FROM {historyStore} WHERE \"MigrationId\" = {SqlLiteral(migration.Id)};");
        }
        else
        {
            builder.AppendLine(idempotent
                ? $"        INSERT INTO {historyStore} (\"MigrationId\") VALUES ({SqlLiteral(migration.Id)});"
                : $"INSERT INTO {historyStore} (\"MigrationId\") VALUES ({SqlLiteral(migration.Id)});");
        }

        if (idempotent)
        {
            builder.AppendLine("    END IF;");
            builder.AppendLine("END $$;");
        }
        else
        {
            builder.AppendLine("COMMIT;");
        }
    }

    private static int FindMigration(IReadOnlyList<MigrationInfo> migrations, string id)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (string.Equals(migrations[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Migration '{id}' is not registered.");
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (builder.Length == 0 || char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal sealed class CommandArguments
    {
        private readonly Dictionary<string, string> _values;
        private readonly HashSet<string> _flags;

        private CommandArguments(Dictionary<string, string> values, HashSet<string> flags, List<string> positional)
        {
            _values = values;
            _flags = flags;
            Positional = positional;
        }

        public List<string> Positional { get; }

        public static CommandArguments Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var positional = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    positional.Add(args[i]);
                    continue;
                }

                var key = args[i][2..];
                if (key is "idempotent" or "force")
                {
                    flags.Add(key);
                    continue;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Missing value for '--{key}'.");
                }

                values[key] = args[++i];
            }

            return new CommandArguments(values, flags, positional);
        }

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public string Require(string key) => Get(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing required option '--{key}'.");

        public string RequireProject()
        {
            var project = Require("project");
            var fullPath = Path.GetFullPath(project);
            if (!File.Exists(fullPath) || !fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Project file '{project}' was not found.");
            }

            return fullPath;
        }

        public bool HasFlag(string key) => _flags.Contains(key);
    }
}

internal sealed record MigrationInfo(string Id, object Instance)
{
    public IReadOnlyList<MigrationCommand> GetCommands(bool down)
    {
        var method = Instance.GetType().GetMethod(down ? "GetDownCommands" : "GetUpCommands")
            ?? throw new InvalidOperationException($"Migration '{Id}' does not expose migration commands.");
        var values = (System.Collections.IEnumerable?)method.Invoke(Instance, null)
            ?? throw new InvalidOperationException($"Migration '{Id}' returned no command list.");
        var result = new List<MigrationCommand>();
        foreach (var value in values)
        {
            var type = value.GetType();
            result.Add(new MigrationCommand(
                (string)(type.GetProperty("Sql")?.GetValue(value) ?? throw new InvalidOperationException("Migration command SQL is missing.")),
                (bool)(type.GetProperty("IsDestructive")?.GetValue(value) ?? false),
                type.GetProperty("Warning")?.GetValue(value) as string));
        }

        return result;
    }
}

internal sealed class ProjectContext : IAsyncDisposable
{
    private ProjectContext(Assembly assembly, Type contextType, object context, Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver)
    {
        Assembly = assembly;
        ContextType = contextType;
        Context = context;
        _resolver = resolver;
    }

    private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> _resolver;
    public Assembly Assembly { get; }
    public Type ContextType { get; }
    public object Context { get; }

    public static async Task<ProjectContext> LoadAsync(string project, MigrationCli.CommandArguments args, string? connection = null)
    {
        var configuration = args.Get("configuration") ?? "Debug";
        var framework = args.Get("framework");
        var build = await RunDotnetAsync("build", project, configuration, framework).ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to build project '{project}'.{Environment.NewLine}{build.Output}");
        }

        var target = await GetTargetPathAsync(project, configuration, framework).ConfigureAwait(false);
        if (!File.Exists(target))
        {
            throw new InvalidOperationException($"Build succeeded but target assembly '{target}' was not found.");
        }

        var dependencyResolver = new AssemblyDependencyResolver(target);
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver = (_, assemblyName) => ResolveDependency(dependencyResolver, assemblyName);
        AssemblyLoadContext.Default.Resolving += resolver;
        var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(item => string.Equals(item.Location, target, StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(target);
        var candidates = GetLoadableTypes(assembly).Where(type => type is { IsClass: true, IsAbstract: false } && IsDbContext(type)).ToArray();
        var contextName = args.Get("context");
        var contextType = string.IsNullOrWhiteSpace(contextName)
            ? candidates.Length == 1
                ? candidates[0]
                : throw new InvalidOperationException(candidates.Length == 0
                    ? $"No DbContext was found in '{target}'."
                    : $"Multiple DbContexts were found in '{target}'. Specify --context.")
            : candidates.SingleOrDefault(type => string.Equals(type.Name, contextName, StringComparison.Ordinal) || string.Equals(type.FullName, contextName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"DbContext '{contextName}' was not found in '{target}'.");

        var instance = CreateContext(contextType, connection);
        return new ProjectContext(assembly, contextType, instance, resolver);
    }

    public MigrationModel GetMigrationModel() => ReadModel(ContextType.GetMethod("GetMigrationModel")?.Invoke(Context, null));

    public MigrationModel? FindSnapshot()
    {
        var snapshots = GetLoadableTypes(Assembly)
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.BaseType?.FullName == "Perigon.PostgreSQL.Migrations.MigrationModelSnapshot")
            .Where(type => type.GetCustomAttributes().Any(attribute => attribute.GetType().FullName == "Perigon.PostgreSQL.Migrations.MigrationSnapshotAttribute" && Equals(attribute.GetType().GetProperty("ContextType")?.GetValue(attribute), ContextType)))
            .ToArray();
        if (snapshots.Length == 0) return null;
        if (snapshots.Length > 1) throw new InvalidOperationException($"Multiple migration snapshots were found for DbContext '{ContextType.FullName}'.");
        var snapshot = Activator.CreateInstance(snapshots[0]) ?? throw new InvalidOperationException("Migration snapshot could not be created.");
        return ReadModel(snapshots[0].GetProperty("Model")?.GetValue(snapshot));
    }

    public IReadOnlyList<MigrationInfo> GetMigrations()
    {
        var registryType = GetCoreAssembly().GetType("Perigon.PostgreSQL.Migrations.MigrationRegistry")
            ?? throw new InvalidOperationException("MigrationRegistry was not found in the loaded project.");
        var method = registryType.GetMethod("GetContextMigrations", [typeof(Type)])
            ?? throw new InvalidOperationException("MigrationRegistry.GetContextMigrations was not found.");
        var registered = (System.Collections.IEnumerable?)method.Invoke(null, [ContextType]) ?? Array.Empty<object>();
        var result = new List<MigrationInfo>();
        foreach (var item in registered)
        {
            var itemType = item.GetType();
            result.Add(new MigrationInfo(
                (string)(itemType.GetProperty("Id")?.GetValue(item) ?? throw new InvalidOperationException("Migration ID is missing.")),
                itemType.GetProperty("Instance")?.GetValue(item) ?? throw new InvalidOperationException("Migration instance is missing.")));
        }
        return result;
    }

    public async Task MigrateAsync(string? target, string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var method = ContextType.GetMethod("MigrateWithDataSourceAsync", [typeof(NpgsqlDataSource), typeof(string), typeof(CancellationToken)]);
        var task = method?.Invoke(Context, [dataSource, target, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("DbContext.MigrateWithDataSourceAsync was not found.");
        await task.ConfigureAwait(false);
    }

    public async Task BaselineAsync(string id, MigrationModel model, string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var coreModelType = GetCoreAssembly().GetType("Perigon.PostgreSQL.Migrations.MigrationModel")!;
        var fromJson = coreModelType.GetMethod("FromJson", BindingFlags.Public | BindingFlags.Static)!;
        var expected = fromJson.Invoke(null, [model.ToJson()]);
        var method = ContextType.GetMethod("BaselineWithDataSourceAsync", [typeof(string), coreModelType, typeof(NpgsqlDataSource), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("DbContext.BaselineWithDataSourceAsync was not found.");
        var task = (Task)method.Invoke(Context, [id, expected, dataSource, CancellationToken.None])!;
        await task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Context is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (Context is IDisposable disposable) disposable.Dispose();
        AssemblyLoadContext.Default.Resolving -= _resolver;
    }

    private static MigrationModel ReadModel(object? value)
    {
        if (value is null) throw new InvalidOperationException("Context did not return a migration model.");
        var json = (string?)value.GetType().GetMethod("ToJson")?.Invoke(value, null)
            ?? throw new InvalidOperationException("Migration model did not expose JSON serialization.");
        return MigrationModel.FromJson(json);
    }

    private Assembly GetCoreAssembly()
    {
        for (var current = ContextType; current is not null; current = current.BaseType)
        {
            if (current.FullName == "Perigon.PostgreSQL.DbContext")
            {
                return current.Assembly;
            }
        }

        throw new InvalidOperationException("The loaded DbContext does not have a Perigon.PostgreSQL base assembly.");
    }

    private static object CreateContext(Type contextType, string? connection)
    {
        var stringConstructor = contextType.GetConstructor([typeof(string)]);
        if (stringConstructor is not null) return stringConstructor.Invoke([connection ?? string.Empty]);
        var parameterless = contextType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null) return parameterless.Invoke(null);
        var optionsConstructor = contextType.GetConstructors().FirstOrDefault(constructor => constructor.GetParameters().Length == 1 && constructor.GetParameters()[0].ParameterType.FullName?.StartsWith("Perigon.PostgreSQL.Options.DbContextOptions", StringComparison.Ordinal) == true);
        if (optionsConstructor is not null) throw new InvalidOperationException($"DbContext '{contextType.FullName}' only has an options constructor. Add a public parameterless or single-string constructor for design-time migrations.");
        throw new InvalidOperationException($"DbContext '{contextType.FullName}' must have a public parameterless or single-string constructor for design-time migrations.");
    }

    private static bool IsDbContext(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType) if (current.FullName == "Perigon.PostgreSQL.DbContext") return true;
        return false;
    }

    private static Assembly? ResolveDependency(AssemblyDependencyResolver resolver, AssemblyName assemblyName)
    {
        var alreadyLoaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyLoaded is not null) return alreadyLoaded;
        var path = resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var frameworkRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", ".."));
        foreach (var sharedFramework in new[] { "Microsoft.AspNetCore.App", "Microsoft.NETCore.App" })
        {
            var root = Path.Combine(frameworkRoot, sharedFramework);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.GetDirectories(root).OrderByDescending(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
        }
        // Calling LoadFromAssemblyName from this Resolving callback re-enters the same callback
        // when the assembly is unavailable, recursively repeating the framework-directory scan.
        return null;
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var loaded = exception.Types.Where(static type => type is not null).Cast<Type>().ToArray();
            if (loaded.Length == 0)
            {
                throw new InvalidOperationException("Unable to load project types. Ensure the target framework runtime is installed.", exception);
            }

            return loaded;
        }
    }

    private static async Task<string> GetTargetPathAsync(string project, string configuration, string? framework)
    {
        var result = await RunDotnetAsync("msbuild", project, configuration, framework, true).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Unable to resolve project target path.{Environment.NewLine}{result.Output}");
        var path = result.Output.Split([Environment.NewLine, "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).LastOrDefault(line => line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        return path is null ? throw new InvalidOperationException("dotnet msbuild did not return TargetPath.") : Path.GetFullPath(path);
    }

    private static async Task<ProcessResult> RunDotnetAsync(string command, string project, string configuration, string? framework, bool getTargetPath = false)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(project)! } };
        process.StartInfo.ArgumentList.Add(command); process.StartInfo.ArgumentList.Add(project); process.StartInfo.ArgumentList.Add("--nologo"); process.StartInfo.ArgumentList.Add("-p:Configuration=" + configuration);
        if (!string.IsNullOrWhiteSpace(framework)) process.StartInfo.ArgumentList.Add("-p:TargetFramework=" + framework);
        if (getTargetPath) process.StartInfo.ArgumentList.Add("-getProperty:TargetPath");
        process.Start(); var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, (await outputTask.ConfigureAwait(false)) + (await errorTask.ConfigureAwait(false)));
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}


internal static class MigrationSourceGenerator
{
    public static string GenerateMigration(string @namespace, Type contextType, string id, string className, MigrationDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("using Perigon.PostgreSQL.Migrations;");
        builder.AppendLine();
        builder.AppendLine($"namespace {@namespace};");
        builder.AppendLine();
        builder.AppendLine($"[Migration(typeof({TypeName(contextType)}), {Literal(id)})]");
        builder.AppendLine($"public sealed class {className} : Migration");
        builder.AppendLine("{");
        AppendMethod(builder, "Up", diff.Up);
        AppendMethod(builder, "Down", diff.Down);
        builder.AppendLine("}");
        return builder.ToString();
    }

    public static string GenerateSnapshot(string @namespace, Type contextType, MigrationModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("using Perigon.PostgreSQL.Migrations;");
        builder.AppendLine("using Perigon.PostgreSQL.Metadata;");
        builder.AppendLine();
        builder.AppendLine($"namespace {@namespace};");
        builder.AppendLine();
        builder.AppendLine($"[MigrationSnapshot(typeof({TypeName(contextType)}))]");
        builder.AppendLine($"public sealed class {contextType.Name}ModelSnapshot : MigrationModelSnapshot");
        builder.AppendLine("{");
        builder.AppendLine("    public override MigrationModel Model => new(new MigrationTable[]");
        builder.AppendLine("    {");
        foreach (var table in model.Tables)
        {
            builder.AppendLine($"        new MigrationTable({NullableLiteral(table.Schema)}, {Literal(table.Name)}, {NullableLiteral(table.Comment)}, new MigrationColumn[]");
            builder.AppendLine("        {");
            foreach (var column in table.Columns)
            {
                builder.AppendLine($"            new MigrationColumn({Literal(column.Name)}, {Literal(column.StoreType)}, {column.IsNullable.ToString().ToLowerInvariant()}, {column.IsIdentity.ToString().ToLowerInvariant()}, {column.IsGenerated.ToString().ToLowerInvariant()}, {NullableLiteral(column.DefaultSql)}, {NullableLiteral(column.GeneratedColumnSql)}, {NullableLiteral(column.Comment)}),");
            }

            builder.AppendLine($"        }}, {PrimaryKeyLiteral(table.PrimaryKey)}, new MigrationForeignKey[]");
            builder.AppendLine("        {");
            foreach (var foreignKey in table.ForeignKeys)
            {
                builder.AppendLine($"            new MigrationForeignKey({Literal(foreignKey.Name)}, {StringArray(foreignKey.Columns)}, {NullableLiteral(foreignKey.PrincipalSchema)}, {Literal(foreignKey.PrincipalTable)}, {StringArray(foreignKey.PrincipalColumns)}, ReferentialAction.{foreignKey.OnDelete}),");
            }

            builder.AppendLine("        }, new MigrationIndex[]");
            builder.AppendLine("        {");
            foreach (var index in table.Indexes)
            {
                builder.AppendLine($"            new MigrationIndex({Literal(index.Name)}, {StringArray(index.Columns)}, {index.IsUnique.ToString().ToLowerInvariant()}, {StringArray(index.IncludeColumns)}, {NullableLiteral(index.Filter)}, {NullableLiteral(index.Method)}),");
            }

            builder.AppendLine("        }),");
        }

        builder.AppendLine("    });");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendMethod(StringBuilder builder, string name, IReadOnlyList<MigrationCommand> commands)
    {
        builder.AppendLine($"    public override void {name}(MigrationBuilder migrationBuilder)");
        builder.AppendLine("    {");
        foreach (var command in commands)
        {
            builder.AppendLine($"        migrationBuilder.Sql({Literal(command.Sql)}, {command.IsDestructive.ToString().ToLowerInvariant()}, {NullableLiteral(command.Warning)});");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static string PrimaryKeyLiteral(MigrationPrimaryKey? key)
    {
        return key is null ? "null" : $"new MigrationPrimaryKey({Literal(key.Name)}, {StringArray(key.Columns)})";
    }

    private static string StringArray(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "global::System.Array.Empty<string>()" : "new string[] { " + string.Join(", ", values.Select(Literal)) + " }";
    }

    private static string NullableLiteral(string? value) => value is null ? "null" : Literal(value);

    private static string TypeName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    private static string Literal(string value) => JsonSerializer.Serialize(value);
}
