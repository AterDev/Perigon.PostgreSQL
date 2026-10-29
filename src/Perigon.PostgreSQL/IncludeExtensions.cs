using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Perigon.PostgreSQL;

/// <summary>
/// Explicit-key split-query loading helpers. These APIs do not inspect or populate
/// EF-style navigation properties; callers provide the key selectors and result shape.
/// </summary>
public static class IncludeExtensions
{
    public static Task<List<TResult>> IncludeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TChild> children,
        Expression<Func<TParent, TKey>> parentKey,
        Expression<Func<TChild, TKey>> childForeignKey,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TChild : class, new()
        where TKey : notnull
        => parents.IncludeManyAsync(children, parentKey, childForeignKey, resultSelector, cancellationToken);

    public static Task<List<TResult>> IncludeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TChild> children,
        Expression<Func<TParent, TKey>> parentKey,
        Expression<Func<TChild, TKey>> childForeignKey,
        Func<IQueryable<TChild>, IQueryable<TChild>>? childQuery,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TChild : class, new()
        where TKey : notnull
        => parents.IncludeManyAsync(children, parentKey, childForeignKey, childQuery, resultSelector, cancellationToken);

    public static Task<List<TResult>> IncludeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TLink,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TParentKey, TChildKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TLink> links,
        DbSet<TChild> children,
        Expression<Func<TParent, TParentKey>> parentKey,
        Expression<Func<TLink, TParentKey>> linkParentKey,
        Expression<Func<TLink, TChildKey>> linkChildKey,
        Expression<Func<TChild, TChildKey>> childKey,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TLink : class, new()
        where TChild : class, new()
        where TParentKey : notnull
        where TChildKey : notnull
        => parents.IncludeManyToManyAsync(links, children, parentKey, linkParentKey, linkChildKey, childKey, resultSelector, cancellationToken);

    public static Task<List<TResult>> IncludeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TLink,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TChild,
        TParentKey, TChildKey, TResult>(
        this IQueryable<TParent> parents,
        DbSet<TLink> links,
        DbSet<TChild> children,
        Expression<Func<TParent, TParentKey>> parentKey,
        Expression<Func<TLink, TParentKey>> linkParentKey,
        Expression<Func<TLink, TChildKey>> linkChildKey,
        Expression<Func<TChild, TChildKey>> childKey,
        Expression<Func<TChild, bool>>? childFilter,
        Func<TParent, List<TChild>, TResult> resultSelector,
        CancellationToken cancellationToken = default)
        where TParent : class, new()
        where TLink : class, new()
        where TChild : class, new()
        where TParentKey : notnull
        where TChildKey : notnull
        => parents.IncludeManyToManyAsync(links, children, parentKey, linkParentKey, linkChildKey, childKey, childFilter, resultSelector, cancellationToken);
}
