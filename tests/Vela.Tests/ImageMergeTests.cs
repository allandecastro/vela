using System;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class ImageMergeTests
    {
        [Fact]
        public void GetPostImageMerged_MergesTargetAttributes()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();
            Entity target = new Entity(TestAccount.EntityLogicalNameValue) { Id = Guid.NewGuid() };
            target["name"] = "Updated Name";
            context.PluginExecutionContext.InputParameters["Target"] = target;

            Entity postImage = new Entity(TestAccount.EntityLogicalNameValue) { Id = target.Id };
            postImage["name"] = "Old Name";
            postImage["telephone1"] = "123";
            context.PluginExecutionContext.PostEntityImages["PostImage"] = postImage;

            Entity merged = context.GetPostImageMerged();
            Assert.Equal("Updated Name", merged.GetAttributeValue<string>("name"));
            Assert.Equal("123", merged.GetAttributeValue<string>("telephone1"));
        }
    }
}
