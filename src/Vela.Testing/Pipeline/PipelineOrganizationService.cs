using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// The pipeline-aware <see cref="IOrganizationService"/> driven by the test and handed to plugins. Write
    /// operations run the registered plugin steps (pre/post stages, cascade); reads delegate to the in-memory store.
    /// Each instance acts as one identity: the test-facing <see cref="PluginPipeline.Service"/> acts as the
    /// configured current user; the services a plugin obtains from <c>IOrganizationServiceFactory</c> act as the
    /// requested user (<c>ctx.Service</c> = the context's UserId, honouring step impersonation) or as SYSTEM
    /// (<c>ctx.SystemService</c>, not subject to the security model). Nested writes cascade through the pipeline.
    /// </summary>
    internal sealed class PipelineOrganizationService : IOrganizationService
    {
        private readonly PluginPipeline _pipeline;
        private readonly PipelineIdentity _identity;

        public PipelineOrganizationService(PluginPipeline pipeline)
            : this(pipeline, PipelineIdentity.Caller)
        {
        }

        public PipelineOrganizationService(PluginPipeline pipeline, PipelineIdentity identity)
        {
            _pipeline = pipeline;
            _identity = identity;
        }

        public Guid Create(Entity entity) => Run(() => _pipeline.ExecuteCreate(entity));

        public void Update(Entity entity) => Run(() => { _pipeline.ExecuteUpdate(entity); return true; });

        public void Delete(string entityName, Guid id) => Run(() => { _pipeline.ExecuteDelete(entityName, id); return true; });

        public OrganizationResponse Execute(OrganizationRequest request) => Run(() => _pipeline.ExecuteRequest(request));

        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => Run(() => { _pipeline.ExecuteAssociate(entityName, entityId, relationship, relatedEntities); return true; });

        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => Run(() => { _pipeline.ExecuteDisassociate(entityName, entityId, relationship, relatedEntities); return true; });

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => Run(() => _pipeline.Store.Retrieve(entityName, id, columnSet));

        public EntityCollection RetrieveMultiple(QueryBase query) => Run(() => _pipeline.Store.RetrieveMultiple(query));

        private T Run<T>(Func<T> body) => _pipeline.RunAs(_identity, body);
    }

    /// <summary>The identity a <see cref="PipelineOrganizationService"/> acts as.</summary>
    internal readonly struct PipelineIdentity
    {
        private PipelineIdentity(bool isCaller, bool isSystem, Guid userId)
        {
            IsCaller = isCaller;
            IsSystem = isSystem;
            UserId = userId;
        }

        /// <summary>The ambient caller (the test-facing service): whatever identity is already in force.</summary>
        public static PipelineIdentity Caller => new PipelineIdentity(true, false, Guid.Empty);

        /// <summary>SYSTEM: not subject to the security model.</summary>
        public static PipelineIdentity System => new PipelineIdentity(false, true, Guid.Empty);

        public static PipelineIdentity User(Guid userId) => new PipelineIdentity(false, false, userId);

        public bool IsCaller { get; }

        public bool IsSystem { get; }

        public Guid UserId { get; }
    }
}
