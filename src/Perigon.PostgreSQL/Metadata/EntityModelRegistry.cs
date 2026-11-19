namespace Perigon.PostgreSQL.Metadata;

public static class EntityModelRegistry
{
    private static readonly Dictionary<Type, EntityModel> Models = new();
    private static readonly Dictionary<Type, IReadOnlyList<EntityModel>> ContextModels = new();
    private static readonly Dictionary<Type, Action<DbContext>> DbSetInitializers = new();
    private static readonly object Sync = new();

    /// <summary>Registers the initializer emitted for a context's automatic DbSet properties.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void RegisterDbSetInitializer<TContext>(Action<TContext> initialize)
        where TContext : DbContext
    {
        lock (Sync)
            DbSetInitializers[typeof(TContext)] = context => initialize((TContext)context);
    }

    internal static void InitializeDbSets(DbContext context)
        => InitializeDbSets(context, context.GetType());

    private static void InitializeDbSets(DbContext context, Type type)
    {
        if (type == typeof(DbContext))
            return;

        InitializeDbSets(context, type.BaseType!);
        Action<DbContext>? initialize;
        lock (Sync)
            DbSetInitializers.TryGetValue(type, out initialize);
        initialize?.Invoke(context);
    }

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
        string? constraintName,
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
                constraintName ?? $"fk_{NamingConventions.ToSnakeCase(dependent.TableName)}_{NamingConventions.ToSnakeCase(dependentColumn.ColumnName)}",
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
                    index.ConfiguredName,
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
