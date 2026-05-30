using Perigon.PostgreSQL.Tools.CodeGeneration;
using Perigon.PostgreSQL.Tools.ReverseEngineering;

return await PerigonTool.RunAsync(args).ConfigureAwait(false);

internal static class PerigonTool
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!IsScaffoldCommand(args))
        {
            WriteUsage();
            return 1;
        }

        try
        {
            var options = ScaffoldOptions.Parse(args.Skip(2).ToArray());
            var catalog = new PostgreSqlCatalogReader(options.ConnectionString);
            var database = await catalog.ReadAsync(options).ConfigureAwait(false);
            var generator = new ScaffoldCodeGenerator(options);
            var files = generator.Generate(database);

            foreach (var warning in database.Warnings)
            {
                Console.Error.WriteLine($"Warning: {warning}");
            }

            Directory.CreateDirectory(options.OutputDirectory);
            foreach (var file in files)
            {
                var path = Path.Combine(options.OutputDirectory, file.RelativePath);
                if (options.DryRun)
                {
                    Console.WriteLine(path);
                    continue;
                }

                if (File.Exists(path) && !options.Force)
                {
                    throw new InvalidOperationException($"File '{path}' already exists. Use --force to overwrite it.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Content).ConfigureAwait(false);
                Console.WriteLine($"Generated {path}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static bool IsScaffoldCommand(string[] args)
    {
        return args.Length >= 2 &&
               ((args[0] == "database" && args[1] == "scaffold") ||
                (args[0] == "dbcontext" && args[1] == "scaffold"));
    }

    private static void WriteUsage()
    {
        Console.WriteLine("Usage: dotnet perigon database scaffold --connection <connection-string> [--context <DbContextName>] [--namespace <namespace>] [--output <directory>] [--schema <schema>] [--table <table>] [--force] [--dry-run] [--no-views]");
    }
}

public sealed class ScaffoldOptions
{
    private readonly HashSet<string> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);

    public required string ConnectionString { get; init; }

    public required string ContextName { get; init; }

    public required string Namespace { get; init; }

    public required string OutputDirectory { get; init; }

    public string ContextRelativeDirectory => "AppDbContext";

    public string ContextRelativePath => Path.Combine(ContextRelativeDirectory, ContextName + ".cs");

    public string EntityRelativeDirectory => "Entity";

    public string EntityNamespace => Namespace + ".Entity";

    public bool Force { get; init; }

    public bool DryRun { get; init; }

    public bool IncludeViews { get; init; } = true;

    public IReadOnlySet<string> Schemas => _schemas;

    public IReadOnlySet<string> Tables => _tables;

    public bool ShouldIncludeSchema(string schema) => _schemas.Count == 0 || _schemas.Contains(schema);

    public bool ShouldIncludeTable(string table) => _tables.Count == 0 || _tables.Contains(table);

    public static ScaffoldOptions Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument '{current}'.");
            }

            var key = current[2..];
            if (key is "force" or "dry-run" or "include-views" or "no-views")
            {
                flags.Add(key);
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Missing value for '--{key}'.");
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(args[++i]);
        }

        static string Required(Dictionary<string, List<string>> values, string key)
        {
            return values.TryGetValue(key, out var list) && list.Count > 0 && !string.IsNullOrWhiteSpace(list[0])
                ? list[0]
                : throw new InvalidOperationException($"Missing required option '--{key}'.");
        }

        static string Optional(Dictionary<string, List<string>> values, string key, string fallback)
        {
            return values.TryGetValue(key, out var list) && list.Count > 0 && !string.IsNullOrWhiteSpace(list[0])
                ? list[0]
                : fallback;
        }

        var options = new ScaffoldOptions
        {
            ConnectionString = Required(values, "connection"),
            ContextName = Optional(values, "context", "DefaultDbContext"),
            Namespace = Optional(values, "namespace", "AppDbContext"),
            OutputDirectory = Optional(values, "output", "."),
            Force = flags.Contains("force"),
            DryRun = flags.Contains("dry-run"),
            IncludeViews = flags.Contains("include-views") || !flags.Contains("no-views")
        };

        foreach (var schema in values.GetValueOrDefault("schema") ?? [])
        {
            options._schemas.Add(schema);
        }

        foreach (var table in values.GetValueOrDefault("table") ?? [])
        {
            options._tables.Add(table);
        }

        return options;
    }
}