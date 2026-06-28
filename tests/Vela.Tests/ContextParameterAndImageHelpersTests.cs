using System;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class ContextParameterAndImageHelpersTests
    {
        [Fact]
        public void GetPreImageMerged_OverlaysTargetOntoPreImage()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();
            Entity target = new Entity(TestAccount.EntityLogicalNameValue) { Id = Guid.NewGuid() };
            target["name"] = "New Name";
            context.PluginExecutionContext.InputParameters["Target"] = target;

            Entity preImage = new Entity(TestAccount.EntityLogicalNameValue) { Id = target.Id };
            preImage["name"] = "Old Name";
            preImage["telephone1"] = "555";
            context.PluginExecutionContext.PreEntityImages["PreImage"] = preImage;

            Entity merged = context.GetPreImageMerged();

            // Target value wins where present; pre-image-only attributes are retained.
            Assert.Equal("New Name", merged.GetAttributeValue<string>("name"));
            Assert.Equal("555", merged.GetAttributeValue<string>("telephone1"));
        }

        [Fact]
        public void GetInputParameter_ReturnsTypedValue_WhenPresent()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();
            context.PluginExecutionContext.InputParameters["Count"] = 5;

            Assert.Equal(5, context.GetInputParameter<int>("Count"));
        }

        [Fact]
        public void GetInputParameter_ReturnsDefault_WhenMissing()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();

            Assert.Null(context.GetInputParameter<string>("Missing"));
            Assert.Equal(0, context.GetInputParameter<int>("Missing"));
        }

        [Fact]
        public void SetOutputParameter_ThenGetOutputParameter_RoundTrips()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();

            context.SetOutputParameter("Score", 99);

            Assert.Equal(99, context.GetOutputParameter<int>("Score"));
            Assert.Equal(99, context.PluginExecutionContext.OutputParameters["Score"]);
        }

        [Fact]
        public void GetOutputParameter_ReturnsDefault_WhenMissing()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();

            Assert.Null(context.GetOutputParameter<string>("Missing"));
        }
    }
}
