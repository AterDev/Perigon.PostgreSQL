namespace Perigon.PostgreSQL.Metadata;

public static class EntityModelRegistry
{
    private static readonly Dictionary<Type, EntityModel> Models = new();
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

    public static bool TryGet(Type entityType, out EntityModel model)
    {
        lock (Sync)
        {
            return Models.TryGetValue(entityType, out model!);
        }
    }
}