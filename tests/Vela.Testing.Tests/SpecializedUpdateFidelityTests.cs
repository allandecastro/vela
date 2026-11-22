using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    // SetStateRequest registers on SetStateDynamicEntity (the message a state change fires); pinning SetState is
    // the only way left to land on the SetState sdkmessage, whose steps never run.
    [Vela.Messages.SdkMessage("SetState")]
    public sealed class PinnedSetState { }

    // #439 row 6: Assign, SetState and owner/state Updates fire the steps the platform fires. Each expectation below was
    // verified live (Dataverse 9.2, account and contact, 2026-10-09):
    //   - AssignRequest: Assign s10, s20, then an inner Update at the same depth (s20, s40 only), then Assign s40.
    //   - Update of ownerid: Update only, never Assign. A mixed Update is ONE execution (no owner/state split).
    //   - SetStateRequest: SetStateDynamicEntity s10, s20, inner Update (s20, s40), SetStateDynamicEntity s40; the
    //     SetState steps never fire; Status -1 reaches SetStateDynamicEntity as sent, the Update Target carries the
    //     resolved status.
    //   - Update of statecode: Update s10, s20, then SetStateDynamicEntity s10, s20, s40 nested at depth 2, then Update
    //     s40; statecode alone leaves the Target without statuscode, SetStateDynamicEntity gets Status -1, and the
    //     record lands on the state's default status.
    public sealed class SpecializedUpdateFidelityTests
    {
        private static readonly Guid OldOwner = new Guid("43900000-0000-0000-0000-00000000a001");
        private static readonly Guid NewOwner = new Guid("43900000-0000-0000-0000-00000000a002");

        private static PluginPipeline Seeded(Guid id)
        {
            return PluginPipeline.Empty().Register<RecordSpecializedSteps>().Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id)
            {
                ["name"] = "Zava",
                ["ownerid"] = new EntityReference("systemuser", OldOwner),
                ["statecode"] = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1),
            });
        }

        private static EntityReference Record(Guid id) => new EntityReference(FakeOwnable.EntityLogicalNameValue, id);

        [Fact]
        public void Assign_fires_the_Assign_steps_around_an_inner_Update_with_no_PreValidation()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id);

            pipeline.Service.Execute(new AssignRequest { Target = Record(id), Assignee = new EntityReference("systemuser", NewOwner) });

            Assert.Equal(
                new[]
                {
                    "Assign s10 d1 pre-owner=a001",
                    "Assign s20 d1 pre-owner=a001",
                    "Update s20 d1 target=[ownerid] pre-owner=a001",
                    "Update s40 d1 target=[ownerid] pre-owner=a001 post-owner=a002",
                    "Assign s40 d1 pre-owner=a001 post-owner=a002",
                },
                RecordSpecializedSteps.Of(id));
            Assert.Equal(NewOwner, pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single().GetAttributeValue<EntityReference>("ownerid").Id);
        }

        [Fact]
        public void Update_of_the_owner_with_a_standard_field_is_one_Update_and_fires_no_Assign_step()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id);

            pipeline.Service.Update(new Entity(FakeOwnable.EntityLogicalNameValue, id)
            {
                ["name"] = "Renamed",
                ["ownerid"] = new EntityReference("systemuser", NewOwner),
            });

            Assert.Equal(
                new[]
                {
                    "Update s10 d1 target=[name,ownerid,vela_ownableid] pre-owner=a001",
                    "Update s20 d1 target=[name,ownerid,vela_ownableid] pre-owner=a001",
                    "Update s40 d1 target=[name,ownerid,vela_ownableid] pre-owner=a001 post-owner=a002",
                },
                RecordSpecializedSteps.Of(id));
        }

        [Fact]
        public void SetState_fires_SetStateDynamicEntity_around_an_inner_Update_and_never_the_SetState_steps()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id);

            pipeline.Service.Execute(new SetStateRequest { EntityMoniker = Record(id), State = new OptionSetValue(1), Status = new OptionSetValue(-1) });

            Assert.Equal(
                new[]
                {
                    "SetStateDynamicEntity s10 d1 state=1 status=-1 pre-state=0/1",
                    "SetStateDynamicEntity s20 d1 state=1 status=-1 pre-state=0/1",
                    "Update s20 d1 target=[statecode,statuscode] status=2 pre-state=0/1",
                    "Update s40 d1 target=[statecode,statuscode] status=2 pre-state=0/1 post-state=1/2",
                    "SetStateDynamicEntity s40 d1 state=1 status=-1 pre-state=0/1 post-state=1/2",
                },
                RecordSpecializedSteps.Of(id));
        }

        [Fact]
        public void SetState_writes_the_Status_a_PreOperation_step_left_in_the_inputs()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<OverrideStatus>().Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id)
            {
                ["statecode"] = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1),
            });

            pipeline.Service.Execute(new SetStateRequest { EntityMoniker = Record(id), State = new OptionSetValue(1), Status = new OptionSetValue(-1) });

            Entity stored = pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single();
            Assert.Equal(1, stored.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(7, stored.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void Update_of_statecode_nests_SetStateDynamicEntity_and_lands_on_the_default_status()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id);

            pipeline.Service.Update(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["statecode"] = new OptionSetValue(1) });

            Assert.Equal(
                new[]
                {
                    "Update s10 d1 target=[statecode,vela_ownableid] pre-state=0/1",
                    "Update s20 d1 target=[statecode,vela_ownableid] pre-state=0/1",
                    "SetStateDynamicEntity s10 d2 state=1 status=-1 pre-state=0/1",
                    "SetStateDynamicEntity s20 d2 state=1 status=-1 pre-state=0/1",
                    "SetStateDynamicEntity s40 d2 state=1 status=-1 pre-state=0/1 post-state=1/2",
                    "Update s40 d1 target=[statecode,vela_ownableid] pre-state=0/1 post-state=1/2",
                },
                RecordSpecializedSteps.Of(id));
        }

        [Fact]
        public void A_state_change_writes_the_standard_fields_first_then_the_state_then_the_owner()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id);

            pipeline.Service.Update(new Entity(FakeOwnable.EntityLogicalNameValue, id)
            {
                ["name"] = "Renamed",
                ["statecode"] = new OptionSetValue(1),
                ["statuscode"] = new OptionSetValue(2),
                ["ownerid"] = new EntityReference("systemuser", NewOwner),
            });

            // The nested SetStateDynamicEntity sees the new name but still the old owner.
            Assert.Contains("SetStateDynamicEntity s40 d2 state=1 status=2 pre-state=0/1 post-state=1/2 post-name=Renamed post-owner=a001", RecordSpecializedSteps.Of(id));
            Entity stored = pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single();
            Assert.Equal(NewOwner, stored.GetAttributeValue<EntityReference>("ownerid").Id);
            Assert.Equal(2, stored.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void The_default_status_of_a_state_is_configurable()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = Seeded(id).WithDefaultStatus(FakeOwnable.EntityLogicalNameValue, 1, 100000001);

            pipeline.Service.Execute(new SetStateRequest { EntityMoniker = Record(id), State = new OptionSetValue(1), Status = new OptionSetValue(-1) });

            Assert.Equal(100000001, pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single().GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        [Fact]
        public void A_SetState_step_failure_rolls_the_state_change_back()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<RejectDeactivation>()
                .Seed(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) });

            Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Update(new Entity(FakeOwnable.EntityLogicalNameValue, id) { ["statecode"] = new OptionSetValue(1) }));

            Assert.Equal(0, pipeline.GetAll(FakeOwnable.EntityLogicalNameValue).Single().GetAttributeValue<OptionSetValue>("statecode").Value);
        }

        [Fact]
        public void The_store_runs_SetState_as_a_state_update_with_the_default_status()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .Seed(new Entity("account", id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) });

            crm.Execute(new SetStateRequest { EntityMoniker = new EntityReference("account", id), State = new OptionSetValue(1), Status = new OptionSetValue(-1) });

            Entity stored = crm.GetAll("account").Single();
            Assert.Equal(1, stored.GetAttributeValue<OptionSetValue>("statecode").Value);
            Assert.Equal(2, stored.GetAttributeValue<OptionSetValue>("statuscode").Value);
        }

        // Records, per record id, each step it ran for: message, stage, depth, the Target's keys, the State/Status
        // inputs, and the owner/state (and name, post) of the full pre/post images.
        public sealed class RecordSpecializedSteps : EntityPluginBase<FakeOwnable>
        {
            private static readonly ConcurrentDictionary<Guid, List<string>> Runs = new ConcurrentDictionary<Guid, List<string>>();

            public RecordSpecializedSteps(string unsecure, string secure) : base(unsecure, secure) { }

            public static string[] Of(Guid id) => Runs.TryGetValue(id, out List<string>? runs) ? runs.ToArray() : new string[0];

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PreValidation().Sync().FilterOn(o => o.Name, o => o.OwnerId, o => o.StateCode).WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(o => o.Name, o => o.OwnerId, o => o.StateCode).WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(o => o.Name, o => o.OwnerId, o => o.StateCode).WithPreImage().WithPostImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnAssign().PreValidation().Sync().WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnAssign().PreOperation().Sync().WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnAssign().PostOperation().Sync().WithPreImage().WithPostImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnMessage<SetStateRequest>().PreValidation().Sync().WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnMessage<SetStateRequest>().PreOperation().Sync().WithPreImage().Handle((Action<ILocalPluginContext>)Record);
                RegisterStep().OnMessage<SetStateRequest>().PostOperation().Sync().WithPreImage().WithPostImage().Handle((Action<ILocalPluginContext>)Record);
#pragma warning disable VELA022 // Deliberate: proves the SetState step never fires.
                RegisterStep().OnMessage<PinnedSetState>().PreOperation().Sync().Handle((Action<ILocalPluginContext>)Record);
#pragma warning restore VELA022
            }

            private void Record(ILocalPluginContext ctx)
            {
                IPluginExecutionContext context = ctx.PluginExecutionContext;
                List<string> parts = new List<string> { $"{context.MessageName} s{context.Stage} d{context.Depth}" };
                if (context.InputParameters.TryGetValue("Target", out object? target) && target is Entity entity)
                {
                    parts.Add($"target=[{string.Join(",", entity.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal))}]");
                    if (entity.GetAttributeValue<OptionSetValue>("statuscode") is OptionSetValue targetStatus)
                    {
                        parts.Add($"status={targetStatus.Value}");
                    }
                }

                if (context.InputParameters.TryGetValue("State", out object? state) && state is OptionSetValue stateValue)
                {
                    parts.Add($"state={stateValue.Value} status={((OptionSetValue)context.InputParameters["Status"]).Value}");
                }

                bool trackOwner = context.MessageName != PluginMessages.SetStateDynamicEntity && !(target is Entity t && t.Contains("statecode"));
                Describe(parts, "pre", context.PreEntityImages.Values.FirstOrDefault(), trackOwner, name: false);
                Describe(parts, "post", context.PostEntityImages.Values.FirstOrDefault(), trackOwner, name: !trackOwner);

                Runs.GetOrAdd(context.PrimaryEntityId, _ => new List<string>()).Add(string.Join(" ", parts));
            }

            private static void Describe(List<string> parts, string label, Entity? image, bool owner, bool name)
            {
                if (image == null)
                {
                    return;
                }

                if (owner)
                {
                    parts.Add($"{label}-owner={OwnerSuffix(image)}");
                    return;
                }

                parts.Add($"{label}-state={image.GetAttributeValue<OptionSetValue>("statecode")?.Value}/{image.GetAttributeValue<OptionSetValue>("statuscode")?.Value}");
                if (name && image.GetAttributeValue<string>("name") == "Renamed")
                {
                    parts.Add($"{label}-name=Renamed {label}-owner={OwnerSuffix(image)}");
                }
            }

            private static string OwnerSuffix(Entity image) =>
                image.GetAttributeValue<EntityReference>("ownerid")?.Id.ToString().Substring(32) ?? "none";
        }

        // Rewrites the Status of a state change at PreOperation, to a status the default would never pick.
        public sealed class OverrideStatus : EntityPluginBase<FakeOwnable>
        {
            public OverrideStatus(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMessage<SetStateRequest>().PreOperation().Sync()
                    .Handle((Action<ILocalPluginContext>)Override);

            private void Override(ILocalPluginContext ctx) => ctx.PluginExecutionContext.InputParameters["Status"] = new OptionSetValue(7);
        }

        // Rejects a deactivation from its nested SetStateDynamicEntity PreOperation step.
        public sealed class RejectDeactivation : EntityPluginBase<FakeOwnable>
        {
            public RejectDeactivation(string unsecure, string secure) : base(unsecure, secure) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnMessage<SetStateRequest>().PreOperation().Sync()
                    .Handle((Action<ILocalPluginContext>)Reject);

            private void Reject(ILocalPluginContext ctx) => throw new InvalidPluginExecutionException("Deactivation rejected.");
        }
    }
}
