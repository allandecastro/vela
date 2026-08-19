using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class SolutionScopedOrganizationServiceTests
{
    // Captures the request the decorator routes through Execute so the SolutionUniqueName parameter can be asserted.
    private sealed class CapturingService : IOrganizationService
    {
        public OrganizationRequest? LastRequest;
        public Guid CreateReturn = Guid.NewGuid();

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            LastRequest = request;
            return request is CreateRequest
                ? new CreateResponse { Results = new ParameterCollection { { "id", CreateReturn } } }
                : new OrganizationResponse();
        }

        public Guid Create(Entity entity) => throw new NotSupportedException();
        public void Update(Entity entity) => throw new NotSupportedException();
        public void Delete(string entityName, Guid id) { }
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }

    [Fact]
    public void Create_routes_through_a_CreateRequest_tagged_with_the_solution()
    {
        var inner = new CapturingService();
        var scoped = new SolutionScopedOrganizationService(inner, "VelaTesting");
        var entity = new Entity("customapi");

        Guid id = scoped.Create(entity);

        Assert.Equal(inner.CreateReturn, id);
        CreateRequest req = Assert.IsType<CreateRequest>(inner.LastRequest);
        Assert.Same(entity, req.Target);
        Assert.Equal("VelaTesting", req["SolutionUniqueName"]);
    }

    [Fact]
    public void Update_routes_through_an_UpdateRequest_tagged_with_the_solution()
    {
        var inner = new CapturingService();
        var scoped = new SolutionScopedOrganizationService(inner, "VelaTesting");
        var entity = new Entity("sdkmessageprocessingstep", Guid.NewGuid());

        scoped.Update(entity);

        UpdateRequest req = Assert.IsType<UpdateRequest>(inner.LastRequest);
        Assert.Same(entity, req.Target);
        Assert.Equal("VelaTesting", req["SolutionUniqueName"]);
    }
}
