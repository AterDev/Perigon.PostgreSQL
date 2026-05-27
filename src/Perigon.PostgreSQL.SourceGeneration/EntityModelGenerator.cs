using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Perigon.PostgreSQL.SourceGeneration;

[Generator]
public sealed class EntityModelGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var dbSetEntities = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, _) => TryReadDbSetInfo(syntaxContext))
            .Where(static dbSet => dbSet is not null);

        var materializerTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => TryReadMaterializerType(syntaxContext))
            .Where(static type => type is not null);

        context.RegisterSourceOutput(
            dbSetEntities.Collect().Combine(materializerTypes.Collect()),
            static (productionContext, input) => Execute(productionContext, input.Left, input.Right));
    }

    private static DbSetInfo? TryReadDbSetInfo(GeneratorSyntaxContext context)
    {
        var property = (PropertyDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(property) is not IPropertySymbol propertySymbol ||
            propertySymbol.Type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } type ||
            type.OriginalDefinition.ToDisplayString() != "Perigon.PostgreSQL.DbSet<T>" ||
            propertySymbol.ContainingType is not { } contextType ||
            !InheritsFrom(contextType, "Perigon.PostgreSQL.DbContext") ||
            !IsAccessibleFromGeneratedCode(contextType) ||
            type.TypeArguments[0] is not INamedTypeSymbol entityType ||
            IsNotMapped(entityType))
        {
            return null;
        }

        return new DbSetInfo(contextType, entityType);
    }

    private static INamedTypeSymbol? TryReadMaterializerType(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type ||
            type.IsAbstract ||
            type.IsGenericType ||
            !IsAccessibleFromGeneratedCode(type) ||
            IsNotMapped(type) ||
            InheritsFrom(type, "Perigon.PostgreSQL.DbContext") ||
            !HasPublicParameterlessConstructor(type))
        {
            return null;
        }

        return type;
    }

    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<DbSetInfo?> dbSets,
        ImmutableArray<INamedTypeSymbol?> materializerTypes)
    {
        var distinct = new Dictionary<string, EntityInfo>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, ContextInfo>(StringComparer.Ordinal);
        var materializers = new Dictionary<string, MaterializerInfo>(StringComparer.Ordinal);
        foreach (var dbSet in dbSets)
        {
            if (dbSet is null)
            {
                continue;
            }

            var info = ReadEntity(dbSet.EntityType);
            distinct[info.FullName] = info;
            materializers[info.FullName] = info.ToMaterializerInfo();

            var contextFullName = dbSet.ContextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!contexts.TryGetValue(contextFullName, out var contextInfo))
            {
                contextInfo = new ContextInfo(contextFullName, SanitizeIdentifier(contextFullName));
                contexts[contextFullName] = contextInfo;
            }

            contextInfo.AddEntity(info.FullName);
        }

        foreach (var type in materializerTypes)
        {
            if (type is null)
            {
                continue;
            }

            var info = ReadMaterializer(type);
            materializers[info.FullName] = info;
        }

        if (distinct.Count == 0 && materializers.Count == 0)
        {
            return;
        }

        var source = GenerateRegistrationSource(
            distinct.Values.OrderBy(entity => entity.FullName, StringComparer.Ordinal),
            contexts.Values.OrderBy(context => context.FullName, StringComparer.Ordinal),
            materializers.Values.OrderBy(type => type.FullName, StringComparer.Ordinal));
        context.AddSource("PerigonEntityModelRegistration.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static EntityInfo ReadEntity(INamedTypeSymbol entity)
    {
        var viewAttribute = FindAttribute(entity.GetAttributes(), "Perigon.PostgreSQL.Attributes.ViewAttribute");
        var tableAttribute = FindAttribute(
            entity.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.TableAttribute",
            "System.ComponentModel.DataAnnotations.Schema.TableAttribute");
        var commentAttribute = FindAttribute(
            entity.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.CommentAttribute",
            "Microsoft.EntityFrameworkCore.CommentAttribute");
        var tableName = ReadStringConstructorArgument(viewAttribute)
            ?? ReadStringConstructorArgument(tableAttribute)
            ?? DefaultTableName(TrimGenericArity(entity.Name));
        var schema = ReadNamedStringArgument(viewAttribute, "Schema") ?? ReadNamedStringArgument(tableAttribute, "Schema");
        var columns = ReadProperties(entity)
            .Where(static property => FindAttribute(
                property.GetAttributes(),
                "Perigon.PostgreSQL.Attributes.NotMappedAttribute",
                "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") is null)
            .Select(property => ReadColumn(entity, property))
            .ToArray();

        return new EntityInfo(
            entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SanitizeIdentifier(entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            Literal(schema),
            Literal(tableName),
            viewAttribute is not null,
            ReadIndexDefinitions(entity),
            Literal(ReadComment(commentAttribute)),
            columns);
    }

    private static MaterializerInfo ReadMaterializer(INamedTypeSymbol type)
    {
        return new MaterializerInfo(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SanitizeIdentifier(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            ReadProperties(type)
                .Where(static property => FindAttribute(
                    property.GetAttributes(),
                    "Perigon.PostgreSQL.Attributes.NotMappedAttribute",
                    "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") is null)
                .Select(property => ReadColumn(type, property))
                .ToArray());
    }

    private static IEnumerable<IPropertySymbol> ReadProperties(INamedTypeSymbol entity)
    {
        var stack = new Stack<INamedTypeSymbol>();
        for (var current = entity; current is not null; current = current.BaseType)
        {
            stack.Push(current);
        }

        while (stack.Count > 0)
        {
            foreach (var property in stack.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                if (!property.IsStatic && property.DeclaredAccessibility == Accessibility.Public && property.GetMethod is not null)
                {
                    yield return property;
                }
            }
        }
    }

    private static ColumnInfo ReadColumn(INamedTypeSymbol entity, IPropertySymbol property)
    {
        var columnAttribute = FindAttribute(
            property.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.ColumnAttribute",
            "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute");
        var keyAttribute = FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.KeyAttribute");
        var requiredAttribute = FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.RequiredAttribute");
        var databaseGeneratedAttribute = FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute");
        var precisionAttribute = FindAttribute(
            property.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.PrecisionAttribute",
            "Microsoft.EntityFrameworkCore.PrecisionAttribute");
        var commentAttribute = FindAttribute(
            property.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.CommentAttribute",
            "Microsoft.EntityFrameworkCore.CommentAttribute");
        var unicodeAttribute = FindAttribute(
            property.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.UnicodeAttribute",
            "Microsoft.EntityFrameworkCore.UnicodeAttribute");
        var databaseGeneratedOption = ReadIntConstructorArgument(databaseGeneratedAttribute);
        var maxLength = ReadIntConstructorArgument(FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.MaxLengthAttribute"));
        if (maxLength is null or < 0)
        {
            maxLength = ReadIntConstructorArgument(FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.StringLengthAttribute"));
        }

        var precision = ReadIntConstructorArgument(precisionAttribute);
        var scale = ReadSecondIntConstructorArgument(precisionAttribute);

        var columnName = ReadStringConstructorArgument(columnAttribute)
            ?? ReadNamedStringArgument(columnAttribute, "Name")
            ?? ToSnakeCase(property.Name);
        var typeName = ReadNamedStringArgument(columnAttribute, "TypeName");
        var isPrimaryKey = ReadNamedBoolArgument(columnAttribute, "IsPrimaryKey") ||
                           keyAttribute is not null ||
                           property.Name == "Id" ||
                           property.Name == entity.Name + "Id";
        var conventionIdentity = databaseGeneratedOption != 0 && isPrimaryKey && IsInteger(property.Type);
        var isIdentity = ReadNamedBoolArgument(columnAttribute, "IsIdentity") ||
                         databaseGeneratedOption == 1 ||
                         conventionIdentity;
        var isGenerated = ReadNamedBoolArgument(columnAttribute, "IsGenerated") || databaseGeneratedOption == 2;
        var isArray = ReadNamedBoolArgument(columnAttribute, "IsArray") || IsArrayLike(property.Type);

        return new ColumnInfo(
            property.Name,
            property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ReadValueType(property.Type),
            Literal(columnName),
            Literal(typeName),
            isPrimaryKey,
            isIdentity,
            isGenerated,
            isArray,
            IsNullable(property, requiredAttribute is not null),
            maxLength,
                precision,
                scale,
                Literal(ReadComment(commentAttribute)),
                ReadUnicode(unicodeAttribute),
            property.SetMethod is { DeclaredAccessibility: Accessibility.Public });
    }

    private static string GenerateRegistrationSource(
        IEnumerable<EntityInfo> entities,
        IEnumerable<ContextInfo> contexts,
        IEnumerable<MaterializerInfo> materializers)
    {
        var entityList = entities.ToArray();
        var contextList = contexts.ToArray();
        var materializerList = materializers.ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Perigon.PostgreSQL.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    internal static class PerigonEntityModelRegistration");
        builder.AppendLine("    {");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("        internal static void Register()");
        builder.AppendLine("        {");

        foreach (var entity in entityList)
        {
            builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.EntityModelRegistry.Register<{entity.FullName}>(");
            builder.AppendLine($"                global::Perigon.PostgreSQL.Metadata.EntityModel.CreateGenerated<{entity.FullName}>(");
            builder.AppendLine($"                    {entity.SchemaLiteral},");
            builder.AppendLine($"                    {entity.TableNameLiteral},");
            builder.AppendLine("                    new global::Perigon.PostgreSQL.Metadata.ColumnModel[]");
            builder.AppendLine("                    {");
            foreach (var column in entity.Columns)
            {
                builder.AppendLine($"                        global::Perigon.PostgreSQL.Metadata.ColumnModel.CreateGenerated<{entity.FullName}, {column.PropertyType}>(");
                builder.AppendLine($"                            {Literal(column.PropertyName)},");
                builder.AppendLine($"                            {column.ColumnNameLiteral},");
                builder.AppendLine($"                            {column.TypeNameLiteral},");
                builder.AppendLine($"                            {BoolLiteral(column.IsPrimaryKey)},");
                builder.AppendLine($"                            {BoolLiteral(column.IsIdentity)},");
                builder.AppendLine($"                            {BoolLiteral(column.IsGenerated)},");
                builder.AppendLine($"                            {BoolLiteral(column.IsArray)},");
                builder.AppendLine($"                            {BoolLiteral(column.IsNullable)},");
                builder.AppendLine($"                            {IntLiteral(column.MaxLength)},");
                builder.AppendLine($"                            {IntLiteral(column.Precision)},");
                builder.AppendLine($"                            {IntLiteral(column.Scale)},");
                builder.AppendLine($"                            {column.CommentLiteral},");
                builder.AppendLine($"                            {NullableBoolLiteral(column.IsUnicode)}),");
            }

            builder.AppendLine("                    },");
            builder.AppendLine($"                    {BoolLiteral(entity.IsView)},");
            builder.AppendLine("                    new global::Perigon.PostgreSQL.Metadata.IndexDefinition[]");
            builder.AppendLine("                    {");
            foreach (var index in entity.Indexes)
            {
                builder.AppendLine("                        new global::Perigon.PostgreSQL.Metadata.IndexDefinition(");
                builder.AppendLine($"                            {Literal(index.Name)},");
                builder.AppendLine("                            new string[]");
                builder.AppendLine("                            {");
                foreach (var propertyName in index.PropertyNames)
                {
                    builder.AppendLine($"                                {Literal(propertyName)},");
                }

                builder.AppendLine("                            },");
                builder.AppendLine($"                            {BoolLiteral(index.IsUnique)}),");
            }

            builder.AppendLine("                    },");
            builder.AppendLine($"                    {entity.CommentLiteral}));");
            builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.EntityValueAccessorRegistry.Register<{entity.FullName}>(");
            builder.AppendLine($"                new global::System.Collections.Generic.Dictionary<string, global::System.Func<{entity.FullName}, object?>>");
            builder.AppendLine("                {");
            foreach (var column in entity.Columns)
            {
                builder.AppendLine($"                    [{Literal(column.PropertyName)}] = static entity => entity.{column.MemberAccessName},");
            }

            builder.AppendLine("                });");
        }

        foreach (var context in contextList)
        {
            builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.EntityModelRegistry.RegisterContext<{context.FullName}>(");
            builder.AppendLine("                new global::Perigon.PostgreSQL.Metadata.EntityModel[]");
            builder.AppendLine("                {");
            foreach (var entityFullName in context.EntityFullNames.OrderBy(static name => name, StringComparer.Ordinal))
            {
                builder.AppendLine($"                    global::Perigon.PostgreSQL.Metadata.EntityModel.For<{entityFullName}>(),");
            }

            builder.AppendLine("                });");
        }

        foreach (var materializer in materializerList)
        {
            builder.AppendLine($"            global::Perigon.PostgreSQL.Execution.EntityMaterializerRegistry.Register<{materializer.FullName}>(Materialize_{materializer.SafeName});");
        }

        builder.AppendLine("        }");

        foreach (var entity in materializerList)
        {
            builder.AppendLine();
            builder.AppendLine($"        private static {entity.FullName} Materialize_{entity.SafeName}(global::Npgsql.NpgsqlDataReader reader)");
            builder.AppendLine("        {");
            builder.AppendLine($"            var entity = new {entity.FullName}();");
            foreach (var column in entity.Columns.Where(static column => column.CanWrite))
            {
                builder.AppendLine($"            var ordinal_{column.SafeName} = TryGetOrdinal(reader, {column.ColumnNameLiteral});");
                builder.AppendLine($"            if (ordinal_{column.SafeName} < 0)");
                builder.AppendLine("            {");
                builder.AppendLine($"                ordinal_{column.SafeName} = TryGetOrdinal(reader, {Literal(column.PropertyName)});");
                builder.AppendLine("            }");
                builder.AppendLine();
                builder.AppendLine($"            if (ordinal_{column.SafeName} >= 0 && !reader.IsDBNull(ordinal_{column.SafeName}))");
                builder.AppendLine("            {");
                builder.AppendLine($"                entity.{column.MemberAccessName} = reader.GetFieldValue<{column.ReadValueType}>(ordinal_{column.SafeName});");
                builder.AppendLine("            }");
                builder.AppendLine();
            }

            builder.AppendLine("            return entity;");
            builder.AppendLine("        }");
        }

        builder.AppendLine();
        builder.AppendLine("        private static int TryGetOrdinal(global::Npgsql.NpgsqlDataReader reader, string name)");
        builder.AppendLine("        {");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                return reader.GetOrdinal(name);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.IndexOutOfRangeException)");
        builder.AppendLine("            {");
        builder.AppendLine("                return -1;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static AttributeData? FindAttribute(ImmutableArray<AttributeData> attributes, params string[] metadataNames)
    {
        return attributes.FirstOrDefault(attribute =>
            attribute.AttributeClass is not null &&
            metadataNames.Contains(attribute.AttributeClass.ToDisplayString(), StringComparer.Ordinal));
    }

    private static string? ReadStringConstructorArgument(AttributeData? attribute)
    {
        return attribute is not null && attribute.ConstructorArguments.Length > 0 &&
               attribute.ConstructorArguments[0].Value is string value
            ? value
            : null;
    }

    private static string? ReadNamedStringArgument(AttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return null;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadIntConstructorArgument(AttributeData? attribute)
    {
        if (attribute is not null && attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int value)
        {
            return value;
        }

        return null;
    }

    private static bool ReadNamedBoolArgument(AttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return false;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is bool value)
            {
                return value;
            }
        }

        return false;
    }

    private static bool? ReadUnicode(AttributeData? attribute)
    {
        if (attribute is null)
        {
            return null;
        }

        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is bool value)
        {
            return value;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "IsUnicode" && argument.Value.Value is bool named)
            {
                return named;
            }
        }

        return true;
    }

    private static int? ReadSecondIntConstructorArgument(AttributeData? attribute)
    {
        if (attribute is not null && attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int value)
        {
            return value;
        }

        return null;
    }

    private static string? ReadComment(AttributeData? attribute)
    {
        if (attribute is null)
        {
            return null;
        }

        return ReadStringConstructorArgument(attribute)
            ?? ReadNamedStringArgument(attribute, "Text")
            ?? ReadNamedStringArgument(attribute, "Comment");
    }

    private static IReadOnlyList<IndexInfo> ReadIndexDefinitions(INamedTypeSymbol entity)
    {
        var indexes = new List<IndexInfo>();
        foreach (var attribute in entity.GetAttributes())
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            if (!IsSupportedIndexAttribute(attributeName) ||
                attribute.ConstructorArguments.Length == 0 ||
                attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
            {
                continue;
            }

            var propertyNames = attribute.ConstructorArguments[0].Values
                .Select(static argument => argument.Value as string)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            if (propertyNames.Length == 0)
            {
                continue;
            }

            indexes.Add(new IndexInfo(
                ReadNamedStringArgument(attribute, "Name"),
                propertyNames,
                ReadNamedBoolArgument(attribute, "IsUnique")));
        }

        return indexes;
    }

    private static bool IsSupportedIndexAttribute(string? attributeName)
    {
        return attributeName is "Perigon.PostgreSQL.Attributes.IndexAttribute" or
            "Microsoft.EntityFrameworkCore.IndexAttribute";
    }

    private static bool IsInteger(ITypeSymbol type)
    {
        var actual = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol nullable
            ? nullable.TypeArguments[0]
            : type;

        return actual.SpecialType is SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64;
    }

    private static bool IsArrayLike(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Array)
        {
            return type is not IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte };
        }

        return type is INamedTypeSymbol { IsGenericType: true } named &&
               named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
    }

    private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type)
    {
        return type.Constructors.Any(static constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0);
    }

    private static bool IsNotMapped(INamedTypeSymbol type)
    {
        return FindAttribute(
            type.GetAttributes(),
            "Perigon.PostgreSQL.Attributes.NotMappedAttribute",
            "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") is not null;
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == metadataName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadValueType(ITypeSymbol type)
    {
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol nullable)
        {
            return nullable.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool IsNullable(IPropertySymbol property, bool isRequired)
    {
        if (isRequired)
        {
            return false;
        }

        if (property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return true;
        }

        return property.Type.IsReferenceType && property.NullableAnnotation == NullableAnnotation.Annotated;
    }

    private static string TrimGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]) ||
                              (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static string DefaultTableName(string typeName)
    {
        var name = ToSnakeCase(typeName);
        return name.EndsWith("s", StringComparison.Ordinal) ? name : name + "s";
    }

    private static string Literal(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string BoolLiteral(bool value)
    {
        return value ? "true" : "false";
    }

    private static string NullableBoolLiteral(bool? value)
    {
        return value is null ? "null" : BoolLiteral(value.Value);
    }

    private static string IntLiteral(int? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var current in value)
        {
            builder.Append(char.IsLetterOrDigit(current) ? current : '_');
        }

        return builder.ToString();
    }

    private static string EscapeIdentifier(string value)
    {
        return SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None &&
               SyntaxFacts.GetContextualKeywordKind(value) == SyntaxKind.None
            ? value
            : "@" + value;
    }

    private sealed class DbSetInfo
    {
        public DbSetInfo(INamedTypeSymbol contextType, INamedTypeSymbol entityType)
        {
            ContextType = contextType;
            EntityType = entityType;
        }

        public INamedTypeSymbol ContextType { get; }

        public INamedTypeSymbol EntityType { get; }
    }

    private sealed class ContextInfo
    {
        private readonly HashSet<string> _entityFullNames = new(StringComparer.Ordinal);

        public ContextInfo(string fullName, string safeName)
        {
            FullName = fullName;
            SafeName = safeName;
        }

        public string FullName { get; }

        public string SafeName { get; }

        public IReadOnlyCollection<string> EntityFullNames => _entityFullNames;

        public void AddEntity(string entityFullName)
        {
            _entityFullNames.Add(entityFullName);
        }
    }

    private sealed class EntityInfo
    {
        public EntityInfo(
            string fullName,
            string safeName,
            string schemaLiteral,
            string tableNameLiteral,
            bool isView,
            IReadOnlyList<IndexInfo> indexes,
            string commentLiteral,
            IReadOnlyList<ColumnInfo> columns)
        {
            FullName = fullName;
            SafeName = safeName;
            SchemaLiteral = schemaLiteral;
            TableNameLiteral = tableNameLiteral;
            IsView = isView;
            Indexes = indexes;
            CommentLiteral = commentLiteral;
            Columns = columns;
        }

        public string FullName { get; }

        public string SafeName { get; }

        public string SchemaLiteral { get; }

        public string TableNameLiteral { get; }

        public bool IsView { get; }

        public IReadOnlyList<IndexInfo> Indexes { get; }

        public string CommentLiteral { get; }

        public IReadOnlyList<ColumnInfo> Columns { get; }

        public MaterializerInfo ToMaterializerInfo()
        {
            return new MaterializerInfo(FullName, SafeName, Columns);
        }
    }

    private sealed class IndexInfo
    {
        public IndexInfo(string? name, IReadOnlyList<string> propertyNames, bool isUnique)
        {
            Name = name;
            PropertyNames = propertyNames;
            IsUnique = isUnique;
        }

        public string? Name { get; }

        public IReadOnlyList<string> PropertyNames { get; }

        public bool IsUnique { get; }
    }

    private sealed class MaterializerInfo
    {
        public MaterializerInfo(string fullName, string safeName, IReadOnlyList<ColumnInfo> columns)
        {
            FullName = fullName;
            SafeName = safeName;
            Columns = columns;
        }

        public string FullName { get; }

        public string SafeName { get; }

        public IReadOnlyList<ColumnInfo> Columns { get; }
    }

    private sealed class ColumnInfo
    {
        public ColumnInfo(
            string propertyName,
            string propertyType,
            string readValueType,
            string columnNameLiteral,
            string typeNameLiteral,
            bool isPrimaryKey,
            bool isIdentity,
            bool isGenerated,
            bool isArray,
            bool isNullable,
            int? maxLength,
            int? precision,
            int? scale,
            string commentLiteral,
            bool? isUnicode,
            bool canWrite)
        {
            PropertyName = propertyName;
            MemberAccessName = EscapeIdentifier(propertyName);
            SafeName = SanitizeIdentifier(propertyName);
            PropertyType = propertyType;
            ReadValueType = readValueType;
            ColumnNameLiteral = columnNameLiteral;
            TypeNameLiteral = typeNameLiteral;
            IsPrimaryKey = isPrimaryKey;
            IsIdentity = isIdentity;
            IsGenerated = isGenerated;
            IsArray = isArray;
            IsNullable = isNullable;
            MaxLength = maxLength;
            Precision = precision;
            Scale = scale;
            CommentLiteral = commentLiteral;
            IsUnicode = isUnicode;
            CanWrite = canWrite;
        }

        public string PropertyName { get; }

        public string MemberAccessName { get; }

        public string SafeName { get; }

        public string PropertyType { get; }

        public string ReadValueType { get; }

        public string ColumnNameLiteral { get; }

        public string TypeNameLiteral { get; }

        public bool IsPrimaryKey { get; }

        public bool IsIdentity { get; }

        public bool IsGenerated { get; }

        public bool IsArray { get; }

        public bool IsNullable { get; }

        public int? MaxLength { get; }

        public int? Precision { get; }

        public int? Scale { get; }

        public string CommentLiteral { get; }

        public bool? IsUnicode { get; }

        public bool CanWrite { get; }
    }
}