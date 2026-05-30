namespace Perigon.PostgreSQL.Metadata;

internal static class RelationshipConventions
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "Explicit ForeignKeyAttribute discovery is a fallback over known DbContext entity types only. Source-generated metadata avoids assembly scanning and convention foreign keys do not require this reflection path.")]
    public static IReadOnlyList<ForeignKeyModel> InferForeignKeys(IReadOnlyList<EntityModel> models)
    {
        var result = models.SelectMany(static model => model.ForeignKeys).ToList();
        foreach (var dependent in models)
        {
            AddExplicitForeignKeys(models, dependent, result);

            foreach (var column in dependent.Columns)
            {
                if (column.IsPrimaryKey || !column.PropertyName.EndsWith("Id", StringComparison.Ordinal) || column.PropertyName.Length <= 2)
                {
                    continue;
                }

                var principalTypeName = column.PropertyName[..^2];
                var principal = models.FirstOrDefault(model =>
                    model.ClrType.Name.Equals(principalTypeName, StringComparison.Ordinal) &&
                    model.PrimaryKeys.Count == 1 &&
                    SameStoreType(column.ClrType, model.PrimaryKeys[0].ClrType));

                if (principal?.PrimaryKeys.Count != 1)
                {
                    continue;
                }

                if (result.Any(existing => existing.DependentEntity == dependent && existing.DependentColumn == column))
                {
                    continue;
                }

                result.Add(new ForeignKeyModel(
                    DefaultForeignKeyName(dependent, column),
                    dependent,
                    column,
                    principal,
                    principal.PrimaryKeys[0]));
            }
        }

        return result;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "Explicit ForeignKeyAttribute discovery is a metadata fallback over known entity types. Source-generated metadata avoids assembly scanning and convention foreign keys do not require attribute reflection.")]
    private static void AddExplicitForeignKeys(IReadOnlyList<EntityModel> models, EntityModel dependent, List<ForeignKeyModel> result)
    {
        foreach (var property in dependent.ClrType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            var attribute = property.GetCustomAttributes(inherit: false)
                .FirstOrDefault(static item => item.GetType().FullName == "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute");
            if (attribute is null)
            {
                continue;
            }

            var name = attribute.GetType().GetProperty("Name")?.GetValue(attribute) as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            ColumnModel? dependentColumn = null;
            EntityModel? principal = null;

            if (dependent.Columns.FirstOrDefault(column => column.PropertyName == property.Name) is { } scalarColumn)
            {
                dependentColumn = scalarColumn;
                var navigation = dependent.ClrType.GetProperty(name);
                principal = navigation is null
                    ? null
                    : models.FirstOrDefault(model => model.ClrType == navigation.PropertyType);
            }
            else if (dependent.Columns.FirstOrDefault(column => column.PropertyName == name) is { } namedColumn)
            {
                dependentColumn = namedColumn;
                principal = models.FirstOrDefault(model => model.ClrType == property.PropertyType);
            }

            if (dependentColumn is null || principal?.PrimaryKeys.Count != 1 || !SameStoreType(dependentColumn.ClrType, principal.PrimaryKeys[0].ClrType))
            {
                continue;
            }

            if (result.Any(existing => existing.DependentEntity == dependent && existing.DependentColumn == dependentColumn))
            {
                continue;
            }

            result.Add(new ForeignKeyModel(
                DefaultForeignKeyName(dependent, dependentColumn),
                dependent,
                dependentColumn,
                principal,
                principal.PrimaryKeys[0]));
        }
    }

    private static bool SameStoreType(Type left, Type right)
    {
        return (Nullable.GetUnderlyingType(left) ?? left) == (Nullable.GetUnderlyingType(right) ?? right);
    }

    private static string DefaultForeignKeyName(EntityModel dependent, ColumnModel column)
    {
        return "fk_" + dependent.TableName + "_" + column.ColumnName;
    }
}