using Microsoft.Xrm.Sdk;

namespace Vela.Querying;

/// <summary>
/// Typed entry point for building queries from an early-bound entity type. Returns a
/// <see cref="TypedQuery{TEntity}"/> so the whole chain is typed without repeating the entity type, and the
/// root entity name is derived from <c>[EntityLogicalName]</c> instead of a magic string.
/// </summary>
public static class Query
{
    /// <summary>
    /// Starts a typed query for <typeparamref name="TEntity"/> (entity name derived from the type).
    /// </summary>
    /// <typeparam name="TEntity">The early-bound entity type.</typeparam>
    public static TypedQuery<TEntity> For<TEntity>()
        where TEntity : Entity
    {
        return new TypedQuery<TEntity>();
    }
}
