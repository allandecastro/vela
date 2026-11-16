using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    // The SYSTEM view of an in-memory store: every call runs as SYSTEM, so it is not subject to the store's security
    // model (WithSecurity), as ctx.SystemService (CreateOrganizationService(null)) is not on a real org. Writes are
    // stamped with the SYSTEM user, on behalf of the store's calling user (createdonbehalfby), as on a real org.
    internal sealed class SystemStoreView : IOrganizationService
    {
        private readonly InMemoryOrganizationService _store;

        public SystemStoreView(InMemoryOrganizationService store)
        {
            _store = store;
        }

        public Guid Create(Entity entity) => AsSystem(() => _store.Create(entity));

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => AsSystem(() => _store.Retrieve(entityName, id, columnSet));

        public void Update(Entity entity) => AsSystem(() => { _store.Update(entity); return true; });

        public void Delete(string entityName, Guid id) => AsSystem(() => { _store.Delete(entityName, id); return true; });

        public OrganizationResponse Execute(OrganizationRequest request) => AsSystem(() => _store.Execute(request));

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => AsSystem(() => { _store.Associate(entityName, entityId, relationship, relatedEntities); return true; });

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => AsSystem(() => { _store.Disassociate(entityName, entityId, relationship, relatedEntities); return true; });

        public EntityCollection RetrieveMultiple(QueryBase query) => AsSystem(() => _store.RetrieveMultiple(query));

        private T AsSystem<T>(Func<T> body) => _store.RunAs(null, body);
    }
}
