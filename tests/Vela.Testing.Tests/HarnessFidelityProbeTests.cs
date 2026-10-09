using System;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Behaviours a live probe (Dataverse 9.2.26094) showed, reproduced by the harness.

    // Two UpdateMultiple steps of one plugin type: each target's image collection holds both steps' images plus the
    // platform's PreBusinessEntity, so a handler must read its image by its own alias.
    public sealed class SiblingBulkSteps : EntityPluginBase<FakeAccount>
    {
        public SiblingBulkSteps(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage("Mine", a => a.Name)
                .Handle(OnName);
            RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.NumberOfEmployees)
                .WithPreImage("Sibling", a => a.NumberOfEmployees)
                .Handle(OnHeadcount);
        }

        private void OnName(IUpdateMultiplePostContext<FakeAccount> ctx)
        {
            EntityImageCollection images = ctx.BulkPreEntityImagesCollection[0];
            string keys = string.Join(",", images.Keys.OrderBy(k => k, StringComparer.Ordinal));
            AccountNameImage? mine = ctx.PreImagesAs<AccountNameImage>("Mine")[0];
            ctx.SystemService.Create(new FakeTask { Subject = $"{keys}|{mine?.Name}" });
        }

        private void OnHeadcount(IUpdateMultiplePostContext<FakeAccount> ctx) { }
    }

    public sealed class HarnessFidelityProbeTests
    {
        [Fact]
        public void A_bulk_image_collection_holds_sibling_step_and_platform_images_and_alias_access_picks_the_own_one()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SiblingBulkSteps>()
                .Seed(new FakeAccount { Id = id, Name = "Old", NumberOfEmployees = 4 });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            string subject = Assert.Single(pipeline.GetAll("task")).GetAttributeValue<string>("subject");
            Assert.Equal("Mine,PreBusinessEntity,Sibling|Old", subject);
        }

    }
}
