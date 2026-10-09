using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// A configurable <see cref="IOrganizationService"/> test double with no mocking-library dependency.
    /// Each operation runs its matching delegate hook; an operation whose hook is not set throws a
    /// <see cref="NotSupportedException"/> naming the hook, so a plugin call the test did not plan for fails at the
    /// call instead of returning a default the plugin then trips over.
    /// Consumers can ignore this type and inject their own <see cref="IOrganizationService"/> via the builder, or
    /// use <see cref="InMemoryOrganizationService"/> for a stateful store.
    /// </summary>
    public sealed class HookOrganizationService : IOrganizationService
    {
        public Func<Entity, Guid>? OnCreate { get; set; }
        public Func<string, Guid, ColumnSet, Entity>? OnRetrieve { get; set; }
        public Action<Entity>? OnUpdate { get; set; }
        public Action<string, Guid>? OnDelete { get; set; }
        public Func<OrganizationRequest, OrganizationResponse>? OnExecute { get; set; }
        public Action<string, Guid, Relationship, EntityReferenceCollection>? OnAssociate { get; set; }
        public Action<string, Guid, Relationship, EntityReferenceCollection>? OnDisassociate { get; set; }
        public Func<QueryBase, EntityCollection>? OnRetrieveMultiple { get; set; }

        public Guid Create(Entity entity)
        {
            return (OnCreate ?? throw Unset(nameof(OnCreate)))(entity);
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            return (OnRetrieve ?? throw Unset(nameof(OnRetrieve)))(entityName, id, columnSet);
        }

        public void Update(Entity entity)
        {
            (OnUpdate ?? throw Unset(nameof(OnUpdate)))(entity);
        }

        public void Delete(string entityName, Guid id)
        {
            (OnDelete ?? throw Unset(nameof(OnDelete)))(entityName, id);
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            return (OnExecute ?? throw Unset(nameof(OnExecute)))(request);
        }

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            (OnAssociate ?? throw Unset(nameof(OnAssociate)))(entityName, entityId, relationship, relatedEntities);
        }

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            (OnDisassociate ?? throw Unset(nameof(OnDisassociate)))(entityName, entityId, relationship, relatedEntities);
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            return (OnRetrieveMultiple ?? throw Unset(nameof(OnRetrieveMultiple)))(query);
        }

        private static NotSupportedException Unset(string hook)
        {
            return new NotSupportedException(
                $"HookOrganizationService.{hook} is not set, so this call has no behaviour. Set {hook} for the call the " +
                $"plugin makes, or use InMemoryOrganizationService for a stateful store.");
        }
    }
}
