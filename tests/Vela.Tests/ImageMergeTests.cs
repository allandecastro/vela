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

        [Fact]
        public void GetPreImageMerged_WithoutTarget_ReturnsIndependentCopy()
        {
            LocalPluginContext context = new PluginContextBuilder().Build();
            Entity preImage = new Entity(TestAccount.EntityLogicalNameValue) { Id = Guid.NewGuid() };
            preImage["name"] = "Original";
            context.PluginExecutionContext.PreEntityImages["PreImage"] = preImage;

            // No Target: the merged result is just the image, but it must be an independent copy, not the shared
            // image reference, so mutating it cannot corrupt the context's PreEntityImages entry.
            Entity merged = context.GetPreImageMerged();
            merged["name"] = "Mutated";

            Assert.Equal("Original", preImage.GetAttributeValue<string>("name"));
        }
    }
}
