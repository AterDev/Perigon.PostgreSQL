using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;
using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL;

/// <summary>Runtime helpers used by source-generated navigation loaders.</summary>
public static class NavigationIncludeLoader
{
    public static async Task LoadCollectionAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild, TKey>(
        DbContext context, List<TParent> parents, string parentProperty, string childForeignProperty, string navigationName, Expression? navigationExpression,
        Action<TParent, List<TChild>> assign, CancellationToken cancellationToken)
        where TParent : class where TChild : class, new() where TKey : notnull
    {
        var parentModel = context.ResolveEntityModel(typeof(TParent));
        var childModel = context.ResolveEntityModel(typeof(TChild));
        var childQuery = FilteredIncludeParser.Parse(navigationExpression, typeof(TChild));
        context.ApplyQueryFilters(typeof(TChild), childQuery);
        ValidateRelationship(context, typeof(TChild), childForeignProperty, typeof(TParent), parentProperty, navigationName, collection: true);
        var parentColumn = parentModel.GetColumn(parentProperty);
        var foreignColumn = childModel.GetColumn(childForeignProperty);
        var parentAccessor = GetAccessor(parentColumn);
        var childAccessor = GetAccessor(foreignColumn);
        var keys = parents.Select(parentAccessor).OfType<TKey>().Distinct().ToArray();
        var children = keys.Length == 0 ? [] : await LoadAsync<TChild, TKey>(context, childModel, foreignColumn, keys, childQuery, cancellationToken).ConfigureAwait(false);
        var lookup = children.ToLookup(child => childAccessor(child));
        foreach (var parent in parents)
        {
            var key = parentAccessor(parent);
            assign(parent, key is null ? [] : lookup[key].ToList());
        }
    }

    public static async Task LoadReferenceAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TDependent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TPrincipal, TKey>(
        DbContext context, List<TDependent> dependents, string foreignProperty, string principalProperty, string navigationName, Expression? navigationExpression,
        Action<TDependent, TPrincipal?> assign, CancellationToken cancellationToken)
        where TDependent : class where TPrincipal : class, new() where TKey : notnull
    {
        if (navigationExpression is not null and not MemberExpression)
            throw new UnsupportedQueryExpressionException("Filtering reference Include is not supported.");
        var dependentModel = context.ResolveEntityModel(typeof(TDependent));
        var principalModel = context.ResolveEntityModel(typeof(TPrincipal));
        var principalQuery = new QueryModel();
        context.ApplyQueryFilters(typeof(TPrincipal), principalQuery);
        ValidateRelationship(context, typeof(TDependent), foreignProperty, typeof(TPrincipal), principalProperty, navigationName, collection: false);
        var foreignAccessor = GetAccessor(dependentModel.GetColumn(foreignProperty));
        var principalColumn = principalModel.GetColumn(principalProperty);
        var principalAccessor = GetAccessor(principalColumn);
        var keys = dependents.Select(foreignAccessor).OfType<TKey>().Distinct().ToArray();
        var principals = keys.Length == 0 ? [] : await LoadAsync<TPrincipal, TKey>(context, principalModel, principalColumn, keys, principalQuery, cancellationToken).ConfigureAwait(false);
        var lookup = principals.ToDictionary(principal => principalAccessor(principal)!, principal => principal);
        foreach (var dependent in dependents)
        {
            var key = foreignAccessor(dependent);
            assign(dependent, key is not null && lookup.TryGetValue(key, out var principal) ? principal : null);
        }
    }

    private static Func<object, object?> GetAccessor(ColumnModel column)
        => EntityValueAccessorRegistry.TryGetAccessor(column.DeclaringType, column.PropertyName, out var accessor)
            ? accessor
            : throw new InvalidOperationException($"Include requires a generated accessor for '{column.DeclaringType.FullName}.{column.PropertyName}'.");

    private static Task<List<TEntity>> LoadAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEntity, TKey>(
        DbContext context, EntityModel model, ColumnModel column, TKey[] keys, QueryModel queryModel, CancellationToken cancellationToken)
        where TEntity : class, new() where TKey : notnull
    {
        var set = new DbSet<TEntity>(context, model);
        var sql = IncludeManyExtensions.BuildChildrenSql(
            set,
            column,
            keys,
            queryModel,
            useCSharpNullSemantics: true,
            applyGlobalFilters: false);
        return CommandExecutor.ExecuteQueryAsync<TEntity>(context, model, sql, cancellationToken);
    }

    private static void ValidateRelationship(DbContext context, Type dependent, string foreignProperty, Type principal, string principalProperty, string navigationName, bool collection)
    {
        var matches = context.GetIncludeEntityModels().SelectMany(model => model.ForeignKeys).Where(fk =>
            fk.DependentEntity.ClrType == dependent && fk.PrincipalEntity.ClrType == principal &&
            fk.DependentColumns.Count == 1 && fk.PrincipalColumns.Count == 1 &&
            fk.DependentColumn.PropertyName == foreignProperty && fk.PrincipalColumn.PropertyName == principalProperty &&
            (collection ? fk.PrincipalNavigationName : fk.DependentNavigationName) == navigationName).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("The generated Include relationship does not match the current context mapping. Configure a matching single-column foreign key.");
    }
}
