using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Perigon.PostgreSQL.Tools.ReverseEngineering;

namespace Perigon.PostgreSQL.Tools.CodeGeneration;

public sealed class ScaffoldCodeGenerator
{
    private readonly ScaffoldOptions _options;

    public ScaffoldCodeGenerator(ScaffoldOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<GeneratedFile> Generate(DatabaseModel database)
    {
        var names = CreateClassNames(database.Tables);
        var files = new List<GeneratedFile> { GenerateDbContext(database, names) };
        foreach (var table in database.Tables)
        {
            files.Add(GenerateEntity(table, names));
        }

        return files;
    }

    private GeneratedFile GenerateDbContext(DatabaseModel database, IReadOnlyDictionary<(string Schema, string Name), string> names)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using Perigon.PostgreSQL;");
        builder.AppendLine();
        builder.AppendLine($"namespace {_options.Namespace};");
        builder.AppendLine();
        builder.AppendLine($"public partial class {_options.ContextName} : DbContext");
        builder.AppendLine("{");
        builder.AppendLine($"    public {_options.ContextName}(string connectionString)");
        builder.AppendLine("        : base(builder => builder.UsePostgres(connectionString))");
        builder.AppendLine("    {");
        builder.AppendLine("    }");
        builder.AppendLine();
        foreach (var table in database.Tables)
        {
            var className = names[(table.Schema, table.Name)];
            builder.AppendLine($"    public DbSet<{className}> {ToPlural(className)} => Set<{className}>();");
        }

        builder.AppendLine("}");
        return new GeneratedFile(_options.ContextName + ".cs", builder.ToString());
    }

    private GeneratedFile GenerateEntity(TableModel table, IReadOnlyDictionary<(string Schema, string Name), string> names)
    {
        var className = names[(table.Schema, table.Name)];
        var propertyNames = CreatePropertyNames(table.Columns);
        var navigationNames = CreateNavigationNames(table, names, propertyNames.Values);
        var builder = new StringBuilder();
        builder.AppendLine("using System;");
        builder.AppendLine("using System.ComponentModel.DataAnnotations;");
        builder.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
        if (table.Indexes.Count > 0)
        {
            builder.AppendLine("using EfCoreIndexAttribute = Microsoft.EntityFrameworkCore.IndexAttribute;");
        }

        if (table.IsView)
        {
            builder.AppendLine("using PerigonViewAttribute = Perigon.PostgreSQL.Attributes.ViewAttribute;");
        }

        builder.AppendLine();
        builder.AppendLine($"namespace {_options.Namespace};");
        builder.AppendLine();
        foreach (var index in table.Indexes)
        {
            if (TryBuildIndexAttribute(index, propertyNames, out var attribute))
            {
                builder.AppendLine(attribute);
            }
        }

        builder.AppendLine(table.IsView
            ? $"[PerigonViewAttribute({Literal(table.Name)}, Schema = {Literal(table.Schema)})]"
            : $"[Table({Literal(table.Name)}, Schema = {Literal(table.Schema)})]");
        builder.AppendLine($"public sealed class {className}");
        builder.AppendLine("{");

        foreach (var column in table.Columns)
        {
            var propertyName = propertyNames[column.Name];
            var isPrimaryKey = table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase);
            var foreignKey = table.ForeignKeys.FirstOrDefault(fk => fk.ColumnName.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            if (isPrimaryKey)
            {
                builder.AppendLine("    [Key]");
            }

            if (column.IsIdentity)
            {
                builder.AppendLine("    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]");
            }
            else if (column.IsGenerated)
            {
                builder.AppendLine("    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]");
            }

            if (foreignKey is not null && navigationNames.TryGetValue(foreignKey, out var navigationName))
            {
                builder.AppendLine($"    [ForeignKey(nameof({navigationName}))]");
            }

            builder.AppendLine($"    [Column({Literal(column.Name)}, TypeName = {Literal(ToStoreType(column))})]");
            if (!column.IsNullable && IsReferenceType(column))
            {
                builder.AppendLine("    [Required]");
            }

            builder.AppendLine($"    public {ToClrType(column)} {propertyName} {{ get; set; }}{DefaultValue(column)}");
            builder.AppendLine();
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            if (!names.TryGetValue((foreignKey.PrincipalSchema, foreignKey.PrincipalTable), out var principalClass) ||
                !navigationNames.TryGetValue(foreignKey, out var navigationName))
            {
                continue;
            }

            builder.AppendLine("    [NotMapped]");
            builder.AppendLine($"    public {principalClass}? {navigationName} {{ get; set; }}");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return new GeneratedFile(className + ".cs", builder.ToString());
    }

    private static IReadOnlyDictionary<(string Schema, string Name), string> CreateClassNames(IReadOnlyList<TableModel> tables)
    {
        var seenBaseNames = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<(string Schema, string Name), string>();
        foreach (var table in tables)
        {
            var baseName = ToClassName(table.Name);
            if (!seenBaseNames.Add(baseName))
            {
                baseName = ToPascalCase(table.Schema) + baseName;
            }

            names[(table.Schema, table.Name)] = MakeUnique(baseName, used);
        }

        return names;
    }

    private static IReadOnlyDictionary<string, string> CreatePropertyNames(IReadOnlyList<ColumnModel> columns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            names[column.Name] = MakeUnique(ToPropertyName(column.Name), used);
        }

        return names;
    }

    private static IReadOnlyDictionary<ForeignKeyModel, string> CreateNavigationNames(
        TableModel table,
        IReadOnlyDictionary<(string Schema, string Name), string> names,
        IEnumerable<string> usedPropertyNames)
    {
        var used = new HashSet<string>(usedPropertyNames, StringComparer.Ordinal);
        var navigationNames = new Dictionary<ForeignKeyModel, string>();
        foreach (var foreignKey in table.ForeignKeys)
        {
            if (!names.TryGetValue((foreignKey.PrincipalSchema, foreignKey.PrincipalTable), out var principalClass))
            {
                continue;
            }

            var duplicatePrincipal = table.ForeignKeys.Count(item =>
                item.PrincipalSchema.Equals(foreignKey.PrincipalSchema, StringComparison.OrdinalIgnoreCase) &&
                item.PrincipalTable.Equals(foreignKey.PrincipalTable, StringComparison.OrdinalIgnoreCase)) > 1;
            var baseName = duplicatePrincipal ? principalClass + ToPropertyName(foreignKey.ColumnName) : principalClass;
            navigationNames[foreignKey] = MakeUnique(baseName, used);
        }

        return navigationNames;
    }

    private static bool TryBuildIndexAttribute(IndexModel index, IReadOnlyDictionary<string, string> propertyNames, out string attribute)
    {
        var properties = new List<string>(index.ColumnNames.Count);
        foreach (var columnName in index.ColumnNames)
        {
            if (!propertyNames.TryGetValue(columnName, out var propertyName))
            {
                attribute = string.Empty;
                return false;
            }

            properties.Add("nameof(" + propertyName + ")");
        }

        if (properties.Count == 0)
        {
            attribute = string.Empty;
            return false;
        }

        var arguments = string.Join(", ", properties);
        var namedArguments = new List<string> { "Name = " + Literal(index.Name) };
        if (index.IsUnique)
        {
            namedArguments.Add("IsUnique = true");
        }

        attribute = "[EfCoreIndexAttribute(" + arguments + ", " + string.Join(", ", namedArguments) + ")]";
        return true;
    }

    private static string ToStoreType(ColumnModel column)
    {
        return column.DataType == "ARRAY" && column.UdtName.StartsWith('_') ? column.UdtName[1..] + "[]" : column.DataType;
    }

    private static string ToClrType(ColumnModel column)
    {
        var nullable = column.IsNullable && !IsReferenceType(column) ? "?" : "";
        var type = column.DataType switch
        {
            "smallint" => "short",
            "integer" => "int",
            "bigint" => "long",
            "real" => "float",
            "double precision" => "double",
            "numeric" => "decimal",
            "boolean" => "bool",
            "uuid" => "Guid",
            "timestamp with time zone" => "DateTime",
            "timestamp without time zone" => "DateTime",
            "date" => "DateOnly",
            "bytea" => "byte[]",
            "jsonb" => "string",
            "json" => "string",
            "ARRAY" => ToArrayClrType(column.UdtName),
            _ => "string"
        };

        return IsReferenceType(column) && column.IsNullable ? type + "?" : type + nullable;
    }

    private static string ToArrayClrType(string udtName)
    {
        var element = udtName.StartsWith('_') ? udtName[1..] : udtName;
        return element switch
        {
            "int2" => "short[]",
            "int4" => "int[]",
            "int8" => "long[]",
            "float4" => "float[]",
            "float8" => "double[]",
            "numeric" => "decimal[]",
            "bool" => "bool[]",
            "uuid" => "Guid[]",
            "timestamptz" => "DateTime[]",
            _ => "string[]"
        };
    }

    private static bool IsReferenceType(ColumnModel column)
    {
        return column.DataType is "text" or "character varying" or "character" or "jsonb" or "json" or "bytea" or "ARRAY" ||
               column.UdtName is "text" or "varchar" or "bpchar";
    }

    private static string DefaultValue(ColumnModel column)
    {
        var type = ToClrType(column).TrimEnd('?');
        return type switch
        {
            "string" => " = \"\";",
            "byte[]" => " = [];",
            var value when value.EndsWith("[]", StringComparison.Ordinal) => " = [];",
            _ => string.Empty
        };
    }

    private static string ToClassName(string tableName)
    {
        var name = ToPascalCase(tableName);
        return name.EndsWith('s') && name.Length > 1 ? name[..^1] : name;
    }

    private static string ToPropertyName(string columnName) => ToPascalCase(columnName);

    private static string ToPlural(string className) => className.EndsWith('s') ? className : className + "s";

    private static string ToPascalCase(string value)
    {
        var builder = new StringBuilder(value.Length);
        var upper = true;
        foreach (var current in value)
        {
            if (!char.IsLetterOrDigit(current))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(current) : current);
            upper = false;
        }

        return MakeIdentifier(builder.ToString(), "Entity");
    }

    private static string MakeIdentifier(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            value = fallback;
        }

        var builder = new StringBuilder(value.Length + 1);
        if (!IsIdentifierStart(value[0]))
        {
            builder.Append('_');
        }

        foreach (var current in value)
        {
            builder.Append(IsIdentifierPart(current) ? current : '_');
        }

        return builder.ToString();
    }

    private static string MakeUnique(string preferredName, HashSet<string> used)
    {
        var name = preferredName;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = preferredName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix++;
        }

        return name;
    }

    private static bool IsIdentifierStart(char value)
    {
        return char.IsLetter(value) || value == '_';
    }

    private static bool IsIdentifierPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static string Literal(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}