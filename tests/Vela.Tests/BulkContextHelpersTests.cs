using System;
using System.Collections.Generic;
using Vela.Context;
using Vela.Constants;
using Microsoft.Xrm.Sdk;
using Moq;
using Xunit;

namespace Vela.Tests;

public sealed class BulkContextHelpersTests
{
    [Fact]
    public void GetTargets_ReturnsBulkTargetsFromContext()
    {
        EntityCollection targets = new EntityCollection
        {
            EntityName = "account",
            Entities =
            {
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" }
            }
        };

        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        context.SetupGet(x => x.BulkTargets).Returns(targets);
        context.SetupGet(x => x.BulkPreEntityImagesCollection).Returns(Array.Empty<EntityImageCollection>());
        context.SetupGet(x => x.BulkPostEntityImagesCollection).Returns(Array.Empty<EntityImageCollection>());

        bool found = context.Object.TryGetTargets(out EntityCollection? actual);

        Assert.True(found);
        Assert.Same(targets, actual);
        Assert.Same(targets, context.Object.GetTargets());
    }

    [Fact]
    public void GetPreImageMerged_ByTargetIndex_MergesImageAndTargetAttributes()
    {
        Guid accountId = Guid.NewGuid();
        Entity target = new Entity("account")
        {
            Id = accountId,
            ["name"] = "New Name"
        };

        Entity preImage = new Entity("account")
        {
            Id = accountId,
            ["name"] = "Old Name",
            ["creditlimit"] = new Money(2000m)
        };

        EntityCollection targets = new EntityCollection { EntityName = "account", Entities = { target } };
        IReadOnlyList<EntityImageCollection> preImageCollections =
            new List<EntityImageCollection>
            {
                new EntityImageCollection
                {
                    [PluginImageAliases.PreImage] = preImage
                }
            };

        Mock<ILocalPluginContext> context = new Mock<ILocalPluginContext>();
        context.SetupGet(x => x.BulkTargets).Returns(targets);
        context.SetupGet(x => x.BulkPreEntityImagesCollection).Returns(preImageCollections);
        context.SetupGet(x => x.BulkPostEntityImagesCollection).Returns(Array.Empty<EntityImageCollection>());

        Entity merged = context.Object.GetPreImageMerged(0);

        Assert.Equal("account", merged.LogicalName);
        Assert.Equal(accountId, merged.Id);
        Assert.Equal("New Name", merged.GetAttributeValue<string>("name"));
        Assert.Equal(2000m, merged.GetAttributeValue<Money>("creditlimit").Value);
    }
}
