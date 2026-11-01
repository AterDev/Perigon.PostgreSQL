namespace Perigon.PostgreSQL;

/// <summary>
/// Legacy optional reset hook for pooled contexts.
///
/// <para>Contexts no longer need to implement this interface to use pooling.
/// Prefer overriding <see cref="DbContext.OnPoolReturn"/>. When an implementation
/// also implements this interface, both hooks are invoked before the context is reused.</para>
/// </summary>
public interface IPooledDbContext
{
    /// <summary>Clears application-owned fields and references. Do not start database operations here.</summary>
    void ResetState();
}
