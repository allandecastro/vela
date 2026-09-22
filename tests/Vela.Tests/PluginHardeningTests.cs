using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

public sealed class PluginHardeningTests
{
    // A self-referential 1:N nav, used to build a cycle (PLUG-001).
    [EntityLogicalName("node")]
    private sealed class CyclicNode : Entity
    {
        public CyclicNode() : base("node") { }

        [Vela.Tests.TestSupport.RelationshipSchemaName("node_children")]
        public IList<CyclicNode> Children { get; set; } = new List<CyclicNode>();
    }

    [Fact]
    public void PopulateRelatedEntities_does_not_overflow_on_a_cyclic_graph()
    {
        CyclicNode a = new CyclicNode { Id = Guid.NewGuid() };
        CyclicNode b = new CyclicNode { Id = Guid.NewGuid() };
        a.Children.Add(b);
        b.Children.Add(a); // cycle

        Entity result = RelationshipProjector.PopulateRelatedEntities(a); // must return, not StackOverflow

        Assert.Same(a, result);
        Assert.True(a.RelatedEntities.Contains(new Relationship("node_children")));
    }

    private sealed class ThrowingStepPlugin(string unsecure, string secure) : EntityPluginBase<TestAccount>(unsecure, secure)
    {
        protected override void RegisterPluginSteps() => throw new InvalidOperationException("step build boom");
    }

    [Fact]
    public void StepDefinitionReader_surfaces_a_failing_step_build_instead_of_dropping_it()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => StepDefinitionReader.GetStepDefinitionFromType(typeof(ThrowingStepPlugin)));
        Assert.Contains("ThrowingStepPlugin", ex.Message);
        Assert.Contains("boom", ex.Message);
    }
}
