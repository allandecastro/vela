using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// The pipeline-aware <see cref="IOrganizationService"/> handed to plugins and driven by the test.
    /// Write operations run the registered plugin steps (pre/post stages, cascade); reads delegate to the
    /// in-memory store. Because plugins receive this same instance, nested writes cascade through the pipeline.
    /// </summary>
    internal sealed class PipelineOrganizationService : IOrganizationService
    {
        private readonly PluginPipeline _pipeline;

        public PipelineOrganizationService(PluginPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        public Guid Create(Entity entity) => _pipeline.ExecuteCreate(entity);

        public void Update(Entity entity) => _pipeline.ExecuteUpdate(entity);

        public void Delete(string entityName, Guid id) => _pipeline.ExecuteDelete(entityName, id);

        public OrganizationResponse Execute(OrganizationRequest request) => _pipeline.ExecuteRequest(request);

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _pipeline.ExecuteAssociate(entityName, entityId, relationship, relatedEntities);

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _pipeline.ExecuteDisassociate(entityName, entityId, relationship, relatedEntities);

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => _pipeline.Store.Retrieve(entityName, id, columnSet);

        public EntityCollection RetrieveMultiple(QueryBase query) => _pipeline.Store.RetrieveMultiple(query);
    }
}
