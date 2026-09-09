using Microsoft.Xrm.Sdk;

namespace Vela.Messages
{
    // Vela-owned typed message requests (POC / Phase 0 of the typed-requests design). Unlike the SDK request
    // types (sealed, Target typed as the base Entity), a Vela request is generic over the entity, so ctx.Request
    // exposes an early-bound Target - the single typed accessor that will replace ctx.Target. The Target getter
    // mirrors GetTarget<T>() exactly (Parameters["Target"].ToEntity<TEntity>()), so behaviour matches the current
    // ctx.Target. See .context/typed-requests-design.md.

    /// <summary>A typed Create request whose <c>Target</c> is the early-bound <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public sealed class CreateRequest<TEntity> : OrganizationRequest where TEntity : Entity
    {
        /// <summary>Creates a Create request over the live <c>InputParameters</c>.</summary>
        public CreateRequest()
        {
            RequestName = "Create";
        }

        /// <summary>The record being created, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }
    }

    /// <summary>A typed Update request whose <c>Target</c> is the early-bound <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The step's entity type.</typeparam>
    public sealed class UpdateRequest<TEntity> : OrganizationRequest where TEntity : Entity
    {
        /// <summary>Creates an Update request over the live <c>InputParameters</c>.</summary>
        public UpdateRequest()
        {
            RequestName = "Update";
        }

        /// <summary>The record being updated, early-bound. Reads/writes <c>Parameters["Target"]</c>.</summary>
        public TEntity Target
        {
            get => Parameters.Contains("Target") && Parameters["Target"] is Entity entity ? entity.ToEntity<TEntity>() : null!;
            set => Parameters["Target"] = value;
        }
    }
}
