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
    private static readonly DiagnosticDescriptor UnsupportedMaterializer = new(
        id: "PGSG001",
        title: "Generated materializer requires a public parameterless constructor",
        messageFormat: "Type '{0}' is not supported by the generated materializer because it has no public parameterless constructor",
        category: "Perigon.PostgreSQL.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMigration = new(
        id: "PGSG002",
        title: "Migration cannot be registered",
        messageFormat: "Migration type '{0}' must have a public parameterless constructor and a valid MigrationAttribute(typeof(DbContext), id)",
        category: "Perigon.PostgreSQL.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var dbSetEntities = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, _) => TryReadDbSetInfo(syntaxContext))
            .Where(static dbSet => dbSet is not null);

        var materializerTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (syntaxContext, _) => TryReadMaterializerType(syntaxContext))
            .Where(static type => type is not null);

        var migrationTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => TryReadMigrationType(syntaxContext))
            .Where(static type => type is not null);

        context.RegisterSourceOutput(
            dbSetEntities.Collect().Combine(materializerTypes.Collect()).Combine(migrationTypes.Collect()),
            static (productionContext, input) => Execute(
                productionContext,
                input.Left.Left,
                input.Left.Right,
                input.Right));
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
            entityType.IsAbstract ||
            IsNotMapped(entityType))
        {
            return null;
        }

        return new DbSetInfo(contextType, entityType);
    }

    private static INamedTypeSymbol? TryReadMaterializerType(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol type ||
            type.IsAbstract ||
            type.IsGenericType ||
            !IsAccessibleFromGeneratedCode(type) ||
            IsNotMapped(type) ||
            InheritsFrom(type, "Perigon.PostgreSQL.DbContext") ||
            InheritsFrom(type, "Perigon.PostgreSQL.Migrations.Migration") ||
            !HasPublicParameterlessConstructor(type))
        {
            return null;
        }

        return type;
    }

    private static INamedTypeSymbol? TryReadMigrationType(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol type ||
            type.IsAbstract ||
            !IsAccessibleFromGeneratedCode(type) ||
            !InheritsFrom(type, "Perigon.PostgreSQL.Migrations.Migration"))
        {
            return null;
        }

        return type;
    }

    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<DbSetInfo?> dbSets,
        ImmutableArray<INamedTypeSymbol?> materializerTypes,
        ImmutableArray<INamedTypeSymbol?> migrationTypes)
    {
        var distinct = new Dictionary<string, EntityInfo>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, ContextInfo>(StringComparer.Ordinal);
        var materializers = new Dictionary<string, MaterializerInfo>(StringComparer.Ordinal);
        var reportedUnsupported = new HashSet<string>(StringComparer.Ordinal);

        void ReportUnsupported(INamedTypeSymbol type)
        {
            if (reportedUnsupported.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMaterializer,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }

        foreach (var dbSet in dbSets)
        {
            if (dbSet is null)
            {
                continue;
            }

            var info = ReadEntity(dbSet.EntityType);
            if (distinct.TryGetValue(info.FullName, out var existing))
            {
                info = existing;
            }
            else
            {
                distinct[info.FullName] = info;
            }
            if (HasPublicParameterlessConstructor(dbSet.EntityType))
            {
                materializers[info.FullName] = info.ToMaterializerInfo();
            }
            else
            {
                ReportUnsupported(dbSet.EntityType);
            }

            var contextFullName = dbSet.ContextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            info.AddContext(contextFullName);
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

        foreach (var type in migrationTypes
                     .Where(static type => type is not null)
                     .GroupBy(static type => type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                     .Select(static group => group.First()))
        {
            if (type is null)
            {
                continue;
            }

            if (!TryReadMigration(type, out var migration))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidMigration,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                continue;
            }

            if (!contexts.TryGetValue(migration.ContextFullName, out var contextInfo))
            {
                contextInfo = new ContextInfo(migration.ContextFullName, SanitizeIdentifier(migration.ContextFullName));
                contexts[migration.ContextFullName] = contextInfo;
            }

            contextInfo.AddMigration(migration);
        }

        if (distinct.Count == 0 && materializers.Count == 0 && contexts.Count == 0)
        {
            return;
        }

        PopulateForeignKeys(distinct.Values);

        var source = GenerateRegistrationSource(
            distinct.Values.OrderBy(entity => entity.FullName, StringComparer.Ordinal),
            contexts.Values.OrderBy(context => context.FullName, StringComparer.Ordinal),
            materializers.Values.OrderBy(type => type.FullName, StringComparer.Ordinal));
        context.AddSource("PerigonEntityModelRegistration.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static bool TryReadMigration(INamedTypeSymbol type, out MigrationInfo migration)
    {
        migration = null!;
        if (type.Arity != 0 || !HasPublicParameterlessConstructor(type))
        {
            return false;
        }

        var attribute = FindAttribute(type.GetAttributes(), "Perigon.PostgreSQL.Migrations.MigrationAttribute");
        if (attribute is null || attribute.ConstructorArguments.Length < 2 ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol contextType ||
            attribute.ConstructorArguments[1].Value is not string id ||
            string.IsNullOrWhiteSpace(id) ||
            contextType.Arity != 0 ||
            !InheritsFrom(contextType, "Perigon.PostgreSQL.DbContext") ||
            !IsAccessibleFromGeneratedCode(contextType))
        {
            return false;
        }

        migration = new MigrationInfo(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            id);
        return true;
    }

    private static void PopulateForeignKeys(IEnumerable<EntityInfo> entities)
    {
        var entityList = entities.ToArray();
        foreach (var dependent in entityList)
        {
            foreach (var column in dependent.Columns)
            {
                if (column.IsPrimaryKey || !column.PropertyName.EndsWith("Id", StringComparison.Ordinal) || column.PropertyName.Length <= 2)
                {
                    continue;
                }

                var columnProperty = FindProperty(dependent.Symbol, column.PropertyName);
                if (columnProperty is not null && columnProperty.GetAttributes().Any(static attribute => attribute.AttributeClass?.Name == "ForeignKeyAttribute"))
                {
                    continue;
                }

                var principalName = column.PropertyName.Substring(0, column.PropertyName.Length - 2);
                var principalCandidates = entityList.Where(entity =>
                    dependent.ContextNames.Overlaps(entity.ContextNames) &&
                    entity.Symbol.Name.Equals(principalName, StringComparison.Ordinal) &&
                    entity.Columns.Count(item => item.IsPrimaryKey) == 1 &&
                    SameType(FindProperty(dependent.Symbol, column.PropertyName)?.Type, FindPrimaryProperty(entity.Symbol, entity.Columns)?.Type)).ToArray();
                var principal = principalCandidates.Length == 1 ? principalCandidates[0] : null;
                if (principal is not null &&
                    (dependent.ContextNames.Count != 1 || principal.ContextNames.Count != 1))
                {
                    principal = null;
                }
                if (principal is not null && FindPrimaryProperty(principal.Symbol, principal.Columns) is { } principalKey)
                {
                    dependent.AddForeignKey(new ForeignKeyInfo(
                        dependent.FullName,
                        principal.FullName,
                        column.PropertyName,
                        principalKey.Name,
                        "fk_" + ToSnakeCase(dependent.TableNameLiteral.Trim('"')) + "_" + ToSnakeCase(column.ColumnNameLiteral.Trim('"')),
                        FindReferenceNavigation(dependent.Symbol, principal.Symbol, principalName),
                        FindCollectionNavigation(principal.Symbol, dependent.Symbol)));
                }
            }

            foreach (var property in ReadProperties(dependent.Symbol))
            {
                var attribute = FindAttribute(property.GetAttributes(), "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute")
                    ?? property.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.Name == "ForeignKeyAttribute");
                var name = ReadStringConstructorArgument(attribute) ?? ReadNamedStringArgument(attribute, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var dependentProperty = dependent.Columns.FirstOrDefault(column => column.PropertyName == name)
                    ?? dependent.Columns.FirstOrDefault(column => column.PropertyName == property.Name);
                var navigationType = property.Type as INamedTypeSymbol;
                var isNavigation = navigationType is not null && entityList.Any(entity => SymbolEquals(entity.Symbol, navigationType));
                var navigation = isNavigation ? property : FindProperty(dependent.Symbol, name!);
                if (dependentProperty is null || navigation?.Type is not INamedTypeSymbol principalType)
                {
                    continue;
                }

                var principal = entityList.FirstOrDefault(entity =>
                    dependent.ContextNames.Overlaps(entity.ContextNames) &&
                    SymbolEquals(entity.Symbol, principalType));
                if (principal is not null &&
                    (dependent.ContextNames.Count != 1 || principal.ContextNames.Count != 1))
                {
                    principal = null;
                }
                var principalKey = principal is null ? null : FindPrimaryProperty(principal.Symbol, principal.Columns);
                if (principal is null || principalKey is null ||
                    principal.Columns.Count(static column => column.IsPrimaryKey) != 1 ||
                    !SameType(FindProperty(dependent.Symbol, dependentProperty.PropertyName)?.Type, principalKey.Type))
                {
                    continue;
                }

                dependent.AddForeignKey(new ForeignKeyInfo(
                    dependent.FullName,
                    principal.FullName,
                    dependentProperty.PropertyName,
                    principalKey.Name,
                    "fk_" + ToSnakeCase(dependent.TableNameLiteral.Trim('"')) + "_" + ToSnakeCase(dependentProperty.ColumnNameLiteral.Trim('"')),
                    isNavigation ? property.Name : name,
                    FindCollectionNavigation(principal.Symbol, dependent.Symbol)));
            }
        }
    }

    private static IPropertySymbol? FindPrimaryProperty(INamedTypeSymbol type, IReadOnlyList<ColumnInfo> columns)
    {
        var name = columns.FirstOrDefault(static column => column.IsPrimaryKey)?.PropertyName;
        return name is null ? null : FindProperty(type, name);
    }

    private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name)
    {
        return ReadProperties(type).FirstOrDefault(property => property.Name == name);
    }

    private static string? FindReferenceNavigation(INamedTypeSymbol dependent, INamedTypeSymbol principal, string preferredName)
    {
        return FindReferenceNavigationProperty(dependent, principal, preferredName)?.Name;
    }

    private static IPropertySymbol? FindReferenceNavigationProperty(INamedTypeSymbol dependent, INamedTypeSymbol principal, string? preferredName)
    {
        return ReadProperties(dependent).FirstOrDefault(property =>
            (preferredName is null || property.Name == preferredName) &&
            property.Type is INamedTypeSymbol named && SymbolEquals(named, principal))
            ?? ReadProperties(dependent).FirstOrDefault(property =>
                property.Type is INamedTypeSymbol named && SymbolEquals(named, principal));
    }

    private static string? FindCollectionNavigation(INamedTypeSymbol principal, INamedTypeSymbol dependent)
    {
        return FindCollectionNavigationProperty(principal, dependent)?.Name;
    }

    private static IPropertySymbol? FindCollectionNavigationProperty(INamedTypeSymbol principal, INamedTypeSymbol dependent, string? preferredName = null)
    {
        foreach (var property in ReadProperties(principal))
        {
            if ((preferredName is null || property.Name == preferredName) &&
                property.Type is INamedTypeSymbol { IsGenericType: true } named &&
                named.TypeArguments.Length == 1 &&
                SymbolEquals(named.TypeArguments[0] as INamedTypeSymbol ?? dependent, dependent))
            {
                return property;
            }
        }

        return null;
    }

    private static bool SameType(ITypeSymbol? left, ITypeSymbol? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(UnwrapNullable(left), UnwrapNullable(right));
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
    }

    private static bool IsNullableType(ITypeSymbol type)
    {
        return type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ||
            (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated);
    }

    private static bool SymbolEquals(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        return SymbolEqualityComparer.Default.Equals(left, right);
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
            entity,
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
        var autoTimestampAttribute = FindAttribute(property.GetAttributes(), "Perigon.PostgreSQL.Attributes.AutoTimestampAttribute");
        var timestampOnInsert = autoTimestampAttribute is not null && ReadBoolConstructorArgument(autoTimestampAttribute, 0, true);
        var timestampOnUpdate = autoTimestampAttribute is not null && ReadBoolConstructorArgument(autoTimestampAttribute, 1, false);

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
            property.SetMethod is { DeclaredAccessibility: Accessibility.Public },
            property.SetMethod?.IsInitOnly == true,
            timestampOnInsert,
            timestampOnUpdate);
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
                builder.AppendLine($"                        global::Perigon.PostgreSQL.Metadata.ColumnModel.CreateGeneratedWithTimestamp<{entity.FullName}, {column.PropertyType}>(");
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
                builder.AppendLine($"                            {NullableBoolLiteral(column.IsUnicode)},");
                builder.AppendLine("                            null,");
                builder.AppendLine("                            null,");
                builder.AppendLine($"                            {BoolLiteral(column.TimestampOnInsert)},");
                builder.AppendLine($"                            {BoolLiteral(column.TimestampOnUpdate)}),");
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
            var writableSetters = entity.Columns
                .Where(static column => column.CanWrite && !column.IsInitOnly)
                .ToArray();
            builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.EntityValueAccessorRegistry.RegisterSetters<{entity.FullName}>(");
            builder.AppendLine("                new global::System.Collections.Generic.Dictionary<string, global::System.Action<" + entity.FullName + ", object?>>");
            builder.AppendLine("                {");
            foreach (var column in writableSetters)
            {
                builder.AppendLine($"                    [{Literal(column.PropertyName)}] = static (entity, value) => entity.{column.MemberAccessName} = value is null ? default! : ({column.PropertyType})value,");
            }

            builder.AppendLine("                });");
        }

        foreach (var entity in entityList)
        {
            foreach (var foreignKey in entity.ForeignKeys)
            {
                builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.EntityModelRegistry.RegisterGeneratedForeignKey<{foreignKey.DependentFullName}, {foreignKey.PrincipalFullName}>(");
                builder.AppendLine($"                {Literal(foreignKey.ConstraintName)}, {Literal(foreignKey.DependentProperty)}, {Literal(foreignKey.PrincipalProperty)}, global::Perigon.PostgreSQL.Metadata.ReferentialAction.NoAction, {Literal(foreignKey.DependentNavigation)}, {Literal(foreignKey.PrincipalNavigation)});");
            }
        }

        foreach (var principal in entityList)
        {
            foreach (var foreignKey in entityList.SelectMany(static entity => entity.ForeignKeys)
                .Where(foreignKey => foreignKey.PrincipalFullName == principal.FullName && foreignKey.PrincipalNavigation is not null))
            {
                var dependentEntity = entityList.First(entity => entity.FullName == foreignKey.DependentFullName);
                if (FindCollectionNavigationProperty(principal.Symbol, dependentEntity.Symbol, foreignKey.PrincipalNavigation!) is not { SetMethod.DeclaredAccessibility: Accessibility.Public } navigation ||
                    navigation.SetMethod.IsInitOnly ||
                    navigation.Type is not INamedTypeSymbol { IsGenericType: true } collectionType ||
                    collectionType.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>" ||
                    !HasPublicParameterlessConstructor(principal.Symbol) ||
                    !HasPublicParameterlessConstructor(dependentEntity.Symbol))
                {
                    continue;
                }

                var dependent = dependentEntity;
                var principalKey = FindProperty(principal.Symbol, foreignKey.PrincipalProperty);
                var dependentKey = FindProperty(dependent.Symbol, foreignKey.DependentProperty);
                if (principalKey is null || dependentKey is null || IsNullableType(principalKey.Type) || !SameType(principalKey.Type, dependentKey.Type))
                {
                    continue;
                }

                foreach (var contextName in principal.ContextNames.Intersect(dependent.ContextNames, StringComparer.Ordinal))
                {
                    builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.NavigationIncludeRegistry.Register<{principal.FullName}>(typeof({contextName}), {Literal(navigation.Name)}, static (context, parents, navigationExpression, cancellationToken) =>");
                    builder.AppendLine($"                global::Perigon.PostgreSQL.NavigationIncludeLoader.LoadCollectionAsync<{principal.FullName}, {dependent.FullName}, {UnwrapNullable(principalKey.Type).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>(context, parents, {Literal(foreignKey.PrincipalProperty)}, {Literal(foreignKey.DependentProperty)}, {Literal(navigation.Name)}, navigationExpression, static (parent, children) => parent.{EscapeIdentifier(navigation.Name)} = children, cancellationToken));");
                }
            }
        }

        foreach (var dependent in entityList)
        {
            foreach (var foreignKey in dependent.ForeignKeys.Where(static foreignKey => foreignKey.DependentNavigation is not null))
            {
                var principal = entityList.FirstOrDefault(entity => entity.FullName == foreignKey.PrincipalFullName);
                var principalKey = principal is null ? null : FindProperty(principal.Symbol, foreignKey.PrincipalProperty);
                var dependentKey = FindProperty(dependent.Symbol, foreignKey.DependentProperty);
                var navigation = principal is null
                    ? null
                    : FindReferenceNavigationProperty(dependent.Symbol, principal.Symbol, foreignKey.DependentNavigation);
                if (principal is null || principalKey is null || dependentKey is null || navigation is null ||
                    IsNullableType(principalKey.Type) ||
                    navigation.SetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    navigation.SetMethod.IsInitOnly ||
                    !HasPublicParameterlessConstructor(dependent.Symbol) ||
                    !HasPublicParameterlessConstructor(principal.Symbol) ||
                    !SameType(principalKey.Type, dependentKey.Type))
                {
                    continue;
                }

                foreach (var contextName in dependent.ContextNames.Intersect(principal.ContextNames, StringComparer.Ordinal))
                {
                    builder.AppendLine($"            global::Perigon.PostgreSQL.Metadata.NavigationIncludeRegistry.Register<{dependent.FullName}>(typeof({contextName}), {Literal(navigation.Name)}, static (context, entities, navigationExpression, cancellationToken) =>");
                    builder.AppendLine($"                global::Perigon.PostgreSQL.NavigationIncludeLoader.LoadReferenceAsync<{dependent.FullName}, {principal.FullName}, {UnwrapNullable(principalKey.Type).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>(context, entities, {Literal(foreignKey.DependentProperty)}, {Literal(foreignKey.PrincipalProperty)}, {Literal(navigation.Name)}, navigationExpression, static (dependent, principal) => dependent.{EscapeIdentifier(navigation.Name)} = principal!, cancellationToken));");
                }
            }
        }

        foreach (var context in contextList)
        {
            if (context.EntityFullNames.Count > 0)
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

            if (context.Migrations.Count > 0)
            {
                builder.AppendLine($"            global::Perigon.PostgreSQL.Migrations.MigrationRegistry.RegisterContext<{context.FullName}>(");
                builder.AppendLine("                new global::Perigon.PostgreSQL.Migrations.RegisteredMigration[]");
                builder.AppendLine("                {");
                foreach (var migration in context.Migrations
                    .OrderBy(static migration => migration.Id, StringComparer.Ordinal)
                    .ThenBy(static migration => migration.FullName, StringComparer.Ordinal))
                {
                    builder.AppendLine($"                    new global::Perigon.PostgreSQL.Migrations.RegisteredMigration({Literal(migration.Id)}, new {migration.FullName}()),");
                }

                builder.AppendLine("                });");
            }
        }

        foreach (var materializer in materializerList)
        {
            builder.AppendLine($"            global::Perigon.PostgreSQL.Execution.EntityMaterializerRegistry.RegisterFactory<{materializer.FullName}>(CreateMaterializer_{materializer.SafeName});");
        }

        builder.AppendLine("        }");

        foreach (var entity in materializerList)
        {
            builder.AppendLine();
            var writableColumns = entity.Columns
                .Where(static column => column.CanWrite)
                .ToArray();
            var materializerArguments = string.Join(", ", writableColumns.Select(static column => $"ordinal_{column.SafeName}"));
            var typedArguments = string.Join(", ", writableColumns.Select(static column => $"typed_{column.SafeName}"));
            var ordinalParameters = string.Join(", ", writableColumns.Select(static column => $"int ordinal_{column.SafeName}"));
            var typedParameters = string.Join(", ", writableColumns.Select(static column => $"bool typed_{column.SafeName}"));
            var materializerParameters = string.Join(", ", new[] { ordinalParameters, typedParameters }.Where(static value => value.Length > 0));

            builder.AppendLine($"        private static global::System.Func<global::Npgsql.NpgsqlDataReader, {entity.FullName}> CreateMaterializer_{entity.SafeName}(global::Npgsql.NpgsqlDataReader reader)");
            builder.AppendLine("        {");
            foreach (var column in writableColumns)
            {
                builder.AppendLine($"            var ordinal_{column.SafeName} = ResolveOrdinal(reader, {column.ColumnNameLiteral}, {Literal(column.PropertyName)});");
                builder.AppendLine($"            var typed_{column.SafeName} = ordinal_{column.SafeName} >= 0 && reader.GetFieldType(ordinal_{column.SafeName}) == typeof({column.ReadValueType});");
            }

            var allMaterializerArguments = string.Join(", ", new[] { "row", materializerArguments, typedArguments }.Where(static value => value.Length > 0));
            builder.AppendLine($"            return row => Materialize_{entity.SafeName}({allMaterializerArguments});");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        private static {entity.FullName} Materialize_{entity.SafeName}(global::Npgsql.NpgsqlDataReader reader{(materializerParameters.Length == 0 ? string.Empty : ", " + materializerParameters)})");
            builder.AppendLine("        {");
            var initColumns = entity.Columns
                .Where(static column => column.CanWrite && column.IsInitOnly)
                .ToArray();
            if (initColumns.Length == 0)
            {
                builder.AppendLine($"            var entity = new {entity.FullName}();");
            }
            else
            {
                builder.AppendLine($"            var entity = new {entity.FullName}");
                builder.AppendLine("            {");
                foreach (var column in initColumns)
                {
                    builder.AppendLine($"                {column.MemberAccessName} = ReadValue<{column.ReadValueType}>(reader, ordinal_{column.SafeName}, typed_{column.SafeName}),");
                }

                builder.AppendLine("            };");
            }

            foreach (var column in entity.Columns.Where(static column => column.CanWrite && !column.IsInitOnly))
            {
                builder.AppendLine($"            if (TryReadValue<{column.ReadValueType}>(reader, ordinal_{column.SafeName}, typed_{column.SafeName}, out var value_{column.SafeName}))");
                builder.AppendLine("            {");
                builder.AppendLine($"                entity.{column.MemberAccessName} = value_{column.SafeName};");
                builder.AppendLine("            }");
                builder.AppendLine();
            }

            builder.AppendLine("            return entity;");
            builder.AppendLine("        }");
        }

        builder.AppendLine();
        builder.AppendLine("        private static int ResolveOrdinal(global::Npgsql.NpgsqlDataReader reader, string columnName, string propertyName)");
        builder.AppendLine("        {");
        builder.AppendLine("            var ordinal = TryGetOrdinal(reader, columnName);");
        builder.AppendLine("            return ordinal >= 0 ? ordinal : TryGetOrdinal(reader, propertyName);");
        builder.AppendLine("        }");
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
        builder.AppendLine();
        builder.AppendLine("        private static T ReadValue<T>(global::Npgsql.NpgsqlDataReader reader, int ordinal, bool useTypedGetter)");
        builder.AppendLine("        {");
        builder.AppendLine("            return TryReadValue<T>(reader, ordinal, useTypedGetter, out var value) ? value : default!;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static bool TryReadValue<T>(global::Npgsql.NpgsqlDataReader reader, int ordinal, bool useTypedGetter, out T value)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (ordinal < 0 || reader.IsDBNull(ordinal))");
        builder.AppendLine("            {");
        builder.AppendLine("                value = default!;");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            value = ReadValueCore<T>(reader, ordinal, useTypedGetter);");
        builder.AppendLine("            return true;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static T ReadValueCore<T>(global::Npgsql.NpgsqlDataReader reader, int ordinal, bool useTypedGetter)");
        builder.AppendLine("        {");
        builder.AppendLine("            // Nullable value types and enums need the conversion path to preserve");
        builder.AppendLine("            // the library's existing PostgreSQL/null/enum semantics.");
        builder.AppendLine("            if (!useTypedGetter)");
        builder.AppendLine("            {");
        builder.AppendLine("                return ConvertValue<T>(reader.GetValue(ordinal));");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                return reader.GetFieldValue<T>(ordinal);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch (global::System.InvalidCastException)");
        builder.AppendLine("            {");
        builder.AppendLine("                return ConvertValue<T>(reader.GetValue(ordinal));");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        private static T ConvertValue<T>(object value)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (value is T typed)");
        builder.AppendLine("            {");
        builder.AppendLine("                return typed;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            var targetType = global::System.Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);");
        builder.AppendLine();
        builder.AppendLine("            if (targetType.IsEnum)");
        builder.AppendLine("            {");
        builder.AppendLine("                return value is string text");
        builder.AppendLine("                    ? (T)global::System.Enum.Parse(targetType, text, true)");
        builder.AppendLine("                    : (T)global::System.Enum.ToObject(targetType, value);");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            if (targetType == typeof(global::System.DateTimeOffset) && value is global::System.DateTime dateTime)");
        builder.AppendLine("            {");
        builder.AppendLine("                var utc = dateTime.Kind == global::System.DateTimeKind.Unspecified");
        builder.AppendLine("                    ? global::System.DateTime.SpecifyKind(dateTime, global::System.DateTimeKind.Utc)");
        builder.AppendLine("                    : dateTime.ToUniversalTime();");
        builder.AppendLine("                return (T)(object)new global::System.DateTimeOffset(utc, global::System.TimeSpan.Zero);");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            return (T)global::System.Convert.ChangeType(value, targetType, global::System.Globalization.CultureInfo.InvariantCulture);");
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

    private static bool ReadBoolConstructorArgument(AttributeData attribute, int index, bool defaultValue)
    {
        return attribute.ConstructorArguments.Length > index &&
            attribute.ConstructorArguments[index].Value is bool value
            ? value
            : defaultValue;
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

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var current in value)
        {
            switch (current)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (char.IsControl(current))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)current).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(current);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
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

        if (builder.Length == 0)
        {
            builder.Append('_');
        }

        return builder.Append('_')
            .Append(StableHash(value).ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
            .ToString();
    }

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var current in value)
        {
            hash ^= current;
            hash *= prime;
        }

        return hash;
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
        private readonly List<MigrationInfo> _migrations = [];

        public ContextInfo(string fullName, string safeName)
        {
            FullName = fullName;
            SafeName = safeName;
        }

        public string FullName { get; }

        public string SafeName { get; }

        public IReadOnlyCollection<string> EntityFullNames => _entityFullNames;

        public IReadOnlyList<MigrationInfo> Migrations => _migrations;

        public void AddEntity(string entityFullName)
        {
            _entityFullNames.Add(entityFullName);
        }

        public void AddMigration(MigrationInfo migration)
        {
            _migrations.Add(migration);
        }
    }

    private sealed class MigrationInfo
    {
        public MigrationInfo(string fullName, string contextFullName, string id)
        {
            FullName = fullName;
            ContextFullName = contextFullName;
            Id = id;
        }

        public string FullName { get; }

        public string ContextFullName { get; }

        public string Id { get; }
    }

    private sealed class EntityInfo
    {
        public EntityInfo(
            INamedTypeSymbol symbol,
            string fullName,
            string safeName,
            string schemaLiteral,
            string tableNameLiteral,
            bool isView,
            IReadOnlyList<IndexInfo> indexes,
            string commentLiteral,
            IReadOnlyList<ColumnInfo> columns)
        {
            Symbol = symbol;
            FullName = fullName;
            SafeName = safeName;
            SchemaLiteral = schemaLiteral;
            TableNameLiteral = tableNameLiteral;
            IsView = isView;
            Indexes = indexes;
            CommentLiteral = commentLiteral;
            Columns = columns;
        }

        public INamedTypeSymbol Symbol { get; }

        public string FullName { get; }

        public string SafeName { get; }

        public string SchemaLiteral { get; }

        public string TableNameLiteral { get; }

        public bool IsView { get; }

        public IReadOnlyList<IndexInfo> Indexes { get; }

        public string CommentLiteral { get; }

        public IReadOnlyList<ColumnInfo> Columns { get; }

        public List<ForeignKeyInfo> ForeignKeys { get; } = [];

        public void AddForeignKey(ForeignKeyInfo foreignKey)
        {
            var existingIndex = ForeignKeys.FindIndex(existing => existing.DependentProperty == foreignKey.DependentProperty);
            if (existingIndex >= 0)
            {
                ForeignKeys[existingIndex] = foreignKey;
            }
            else
            {
                ForeignKeys.Add(foreignKey);
            }
        }

        public HashSet<string> ContextNames { get; } = new(StringComparer.Ordinal);

        public void AddContext(string contextFullName)
        {
            ContextNames.Add(contextFullName);
        }

        public MaterializerInfo ToMaterializerInfo()
        {
            return new MaterializerInfo(FullName, SafeName, Columns);
        }
    }

    private sealed class ForeignKeyInfo
    {
        public ForeignKeyInfo(string dependentFullName, string principalFullName, string dependentProperty, string principalProperty, string constraintName, string? dependentNavigation, string? principalNavigation)
        {
            DependentFullName = dependentFullName;
            PrincipalFullName = principalFullName;
            DependentProperty = dependentProperty;
            PrincipalProperty = principalProperty;
            ConstraintName = constraintName;
            DependentNavigation = dependentNavigation;
            PrincipalNavigation = principalNavigation;
        }

        public string DependentFullName { get; }
        public string PrincipalFullName { get; }
        public string DependentProperty { get; }
        public string PrincipalProperty { get; }
        public string ConstraintName { get; }
        public string? DependentNavigation { get; }
        public string? PrincipalNavigation { get; }
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
            bool canWrite,
            bool isInitOnly,
            bool timestampOnInsert,
            bool timestampOnUpdate)
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
            IsInitOnly = isInitOnly;
            TimestampOnInsert = timestampOnInsert;
            TimestampOnUpdate = timestampOnUpdate;
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

        public bool IsInitOnly { get; }

        public bool TimestampOnInsert { get; }

        public bool TimestampOnUpdate { get; }
    }
}
