namespace Perigon.PostgreSQL.Metadata;

public static class EntityModelRegistry
{
    private static readonly Dictionary<Type, EntityModel> Models = new();
    private static readonly Dictionary<Type, IReadOnlyList<EntityModel>> ContextModels = new();
    private static readonly object Sync = new();

    public static void Register<T>(EntityModel model)
        where T : class
    {
        if (model.ClrType != typeof(T))
        {
            throw new ArgumentException($"Model CLR type '{model.ClrType.FullName}' does not match registration type '{typeof(T).FullName}'.", nameof(model));
        }

        lock (Sync)
        {
            Models[typeof(T)] = model;
        }
    }

    public static void RegisterGeneratedForeignKey<TDependent, TPrincipal>(
        string constraintName,
        string dependentPropertyName,
        string principalPropertyName,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        string? dependentNavigationName = null,
        string? principalNavigationName = null)
        where TDependent : class
        where TPrincipal : class
    {
        lock (Sync)
        {
            if (!Models.TryGetValue(typeof(TDependent), out var dependent) ||
                !Models.TryGetValue(typeof(TPrincipal), out var principal))
            {
                return;
            }

            var dependentColumn = dependent.GetColumn(dependentPropertyName);
            var principalColumn = principal.GetColumn(principalPropertyName);
            if (dependent.ForeignKeys.Any(existing => existing.DependentColumn == dependentColumn))
            {
                return;
            }

            var foreignKey = new ForeignKeyModel(
                constraintName,
                dependent,
                dependentColumn,
                principal,
                principalColumn,
                onDelete,
                dependentNavigationName,
                principalNavigationName);
            Models[typeof(TDependent)] = dependent.Apply(
                dependent.Schema,
                dependent.TableName,
                dependent.Columns,
                dependent.Comment,
                dependent.Indexes.Select(static index => new IndexDefinition(
                    index.IndexName,
                    index.Columns.Select(static column => column.PropertyName).ToArray(),
                    index.IsUnique,
                    index.IncludeColumns.Select(static column => column.PropertyName).ToArray(),
                    index.Filter,
                    index.Method)).ToArray(),
                dependent.ForeignKeys.Concat([foreignKey]).ToArray());
        }
    }

    public static bool TryGet(Type entityType, out EntityModel model)
    {
        lock (Sync)
        {
            return Models.TryGetValue(entityType, out model!);
        }
    }

    public static void RegisterContext<TContext>(IReadOnlyList<EntityModel> models)
        where TContext : DbContext
    {
        lock (Sync)
        {
            ContextModels[typeof(TContext)] = models.ToArray();
        }
    }

    public static IReadOnlyList<EntityModel> GetContextModels(Type contextType)
    {
        lock (Sync)
        {
            return ContextModels.TryGetValue(contextType, out var models)
                ? models
                : Array.Empty<EntityModel>();
        }
    }
}
