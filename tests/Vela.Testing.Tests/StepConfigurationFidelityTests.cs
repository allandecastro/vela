using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: the pipeline must honour what a step's registration says (enabled flag, configuration,
    // impersonation), the identities of ctx.Service / ctx.SystemService, the global Associate/Disassociate
    // messages, Upsert and alternate-key addressing, and a nested request's own bypass parameters. Each of these
    // previously let a test pass while the same plugin would behave differently on a real org.
    public sealed class StepConfigurationFidelityTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000201");
        private static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000202");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000002b1");

        private static Entity SingleCapture(PluginPipeline pipeline) => Assert.Single(pipeline.GetAll("vela_capture"));

        // --- A-15: step configuration --------------------------------------------------------------------

        [Fact]
        public void A_disabled_step_does_not_fire()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ScDisabledOnCreate>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Assert.Empty(pipeline.ExecutionLog);
            Assert.Empty(pipeline.GetAll("vela_capture"));
            Assert.Single(pipeline.GetAll("account")); // the write itself still happens
        }

        [Fact]
        public void The_unsecure_and_secure_configuration_reach_the_plugin_constructor()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<ScCaptureConfig>()
                .WithSecureConfig(ScCaptureConfig.SecureName, "s3cret");

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity capture = SingleCapture(pipeline);
            Assert.Equal(ScCaptureConfig.Unsecure, capture.GetAttributeValue<string>("unsecure"));
            Assert.Equal("s3cret", capture.GetAttributeValue<string>("secure"));
        }

        [Fact]
        public void An_impersonated_step_sees_the_impersonated_user_and_acts_as_that_user()
        {
            // Alice may create accounts but not tasks; Bob (the step's impersonated user) may create tasks.
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("accounts", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                    .Role("tasks", r => r.Grant("task", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "accounts")
                    .User(Bob, SalesBu, "tasks"))
                .Register<ScImpersonatedAsBob>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal($"user:{Bob};initiating:{Alice}", task.GetAttributeValue<string>("subject"));
            Assert.Equal(Bob, task.GetAttributeValue<EntityReference>("createdby").Id); // written as Bob
        }

        [Fact]
        public void Under_security_the_system_service_is_unrestricted_while_the_user_service_is_enforced()
        {
            static PluginPipeline Build<TPlugin>() where TPlugin : IPlugin => PluginPipeline.Empty()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("accounts", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "accounts")) // no task privilege at all
                .Register<TPlugin>();

            PluginPipeline system = Build<ScTaskViaSystemService>();
            system.Service.Create(new FakeAccount { Name = "Zava" });
            Assert.Single(system.GetAll("task"));

            PluginPipeline user = Build<ScTaskViaUserService>();
            // The plugin base rethrows the denial (PrincipalPrivilegeDenied) unchanged, so the caller keeps its code.
            FaultException<OrganizationServiceFault> denied = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => user.Service.Create(new FakeAccount { Name = "Zava" }));
            Assert.Contains("is missing prvCreatetask privilege", denied.Detail.Message);
            Assert.Empty(user.GetAll("task"));
            Assert.Empty(user.GetAll("account")); // the denied plugin write rolled the whole operation back
        }

        // --- A-16: Associate/Disassociate are global messages ----------------------------------------------

        [Fact]
        public void Associate_fires_a_step_registered_on_another_entity()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId })
                .Register<ScAssociateOnAccount>();

            // Associated from the CONTACT side: the step was declared on account, but Associate has no entity filter.
            pipeline.Service.Associate("contact", contactId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("account", accountId) });

            PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog);
            Assert.Equal(PluginMessages.Associate, entry.Message);
            Assert.Equal($"associate:contact:{contactId}", SingleCapture(pipeline).GetAttributeValue<string>("subject"));
        }

        // On Dataverse a global message runs on the "none" sdkmessagefilter: every Associate/Disassociate step sees
        // PrimaryEntityName "none" and an empty PrimaryEntityId, at every stage and mode (verified live, #459). Only
        // the Target input names the record, so a handler branching on PrimaryEntityName == "account" never matches.
        [Theory]
        [InlineData("Associate")]
        [InlineData("Disassociate")]
        public void Global_message_steps_see_primary_entity_none_and_the_target_reference(string message)
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid")
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });
            Relationship relationship = new Relationship("account_contacts");
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("contact", contactId) };
            if (message == PluginMessages.Disassociate)
            {
                pipeline.Service.Associate("account", accountId, relationship, related);
            }

            pipeline.Register<ScCaptureGlobalContext>();
            if (message == PluginMessages.Associate)
            {
                pipeline.Service.Associate("account", accountId, relationship, related);
            }
            else
            {
                pipeline.Service.Disassociate("account", accountId, relationship, related);
            }

            string[] captured = pipeline.GetAll("vela_capture").Select(c => c.GetAttributeValue<string>("subject")).OrderBy(s => s).ToArray();
            Assert.Equal(new[]
            {
                $"{message}:10:0:none:{Guid.Empty}:account:{accountId}",
                $"{message}:40:0:none:{Guid.Empty}:account:{accountId}",
                $"{message}:40:1:none:{Guid.Empty}:account:{accountId}",
            }, captured);
        }

        // --- A-17: Upsert and alternate keys ------------------------------------------------------------

        [Fact]
        public void Upsert_creates_then_updates_firing_the_matching_steps()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithAlternateKey("account", "accountnumber")
                .Register<ScCreatePost>()
                .Register<ScUpdatePost>();

            UpsertResponse created = (UpsertResponse)pipeline.Service.Execute(new UpsertRequest
            {
                Target = new Entity("account", "accountnumber", "A-1") { ["name"] = "First" }
            });

            Assert.True(created.RecordCreated);
            Assert.Equal(new[] { PluginMessages.Create }, pipeline.ExecutionLog.Select(e => e.Message).ToArray());
            Entity stored = Assert.Single(pipeline.GetAll("account"));
            Assert.Equal(stored.Id, created.Target.Id);
            Assert.Equal("A-1", stored.GetAttributeValue<string>("accountnumber"));

            UpsertResponse updated = (UpsertResponse)pipeline.Service.Execute(new UpsertRequest
            {
                Target = new Entity("account", "accountnumber", "A-1") { ["name"] = "Second" }
            });

            Assert.False(updated.RecordCreated);
            Assert.Equal(stored.Id, updated.Target.Id);
            Assert.Equal(new[] { PluginMessages.Create, PluginMessages.Update }, pipeline.ExecutionLog.Select(e => e.Message).ToArray());
            Assert.Equal("Second", Assert.Single(pipeline.GetAll("account")).GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Update_and_Delete_by_alternate_key_fire_their_steps_with_the_resolved_id()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithAlternateKey("account", "accountnumber")
                .Seed(new Entity("account", id) { ["accountnumber"] = "A-1", ["name"] = "Old" })
                .Register<ScCaptureUpdateId>()
                .Register<ScCaptureDeleteId>();

            pipeline.Service.Execute(new UpdateRequest { Target = new Entity("account", "accountnumber", "A-1") { ["name"] = "New" } });
            pipeline.Service.Execute(new DeleteRequest { Target = new EntityReference("account", "accountnumber", "A-1") });

            string[] subjects = pipeline.GetAll("vela_capture").Select(c => c.GetAttributeValue<string>("subject")).OrderBy(s => s).ToArray();
            Assert.Equal(new[] { $"delete:{id}", $"update:{id}:{id}" }, subjects);
            Assert.Empty(pipeline.GetAll("account"));
        }

        // --- A-18: a nested request's own bypass --------------------------------------------------------

        [Fact]
        public void A_nested_write_with_BypassCustomPluginExecution_does_not_re_enter_the_plugin()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Seed(new FakeAccount { Id = id, Name = "Old" })
                .Register<ScSelfUpdateWithBypass>();

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            // Only the outer update fired the step; the plugin's own bypassed write did not re-enter it (before the
            // fix the nested bypass was ignored and the plugin recursed until the depth cap aborted the operation).
            PipelineExecutionEntry entry = Assert.Single(pipeline.ExecutionLog);
            Assert.Equal(1, entry.Depth);
            Assert.Equal("stamped", Assert.Single(pipeline.GetAll("account")).GetAttributeValue<string>("name"));
        }
    }

    public sealed class ScDisabledOnCreate : EntityPluginBase<FakeAccount>
    {
        public ScDisabledOnCreate(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Disabled()
                .Handle(ctx => ctx.SystemService.Create(new Entity("vela_capture") { ["subject"] = "fired" }));
    }

    public sealed class ScCaptureConfig : EntityPluginBase<FakeAccount>
    {
        public const string Unsecure = "{\"mode\":\"strict\"}";
        public const string SecureName = "ApiKey";
        private readonly string _unsecure;
        private readonly string _secure;

        public ScCaptureConfig(string u, string s) : base(u, s)
        {
            _unsecure = u;
            _secure = s;
        }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .WithUnsecureConfig(Unsecure).WithSecureConfigFrom(SecureName)
                .Handle(ctx => ctx.SystemService.Create(new Entity("vela_capture") { ["unsecure"] = _unsecure, ["secure"] = _secure }));
    }

    public sealed class ScImpersonatedAsBob : EntityPluginBase<FakeAccount>
    {
        public static readonly Guid Bob = new Guid("b0b00000-0000-0000-0000-000000000202");
        public ScImpersonatedAsBob(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).WithImpersonation(Bob)
                .Handle(ctx => ctx.Service.Create(new Entity("task") { ["subject"] = $"user:{ctx.UserId};initiating:{ctx.InitiatingUserId}" }));
    }

    public sealed class ScTaskViaSystemService : EntityPluginBase<FakeAccount>
    {
        public ScTaskViaSystemService(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.SystemService.Create(new Entity("task") { ["subject"] = "system" }));
    }

    public sealed class ScTaskViaUserService : EntityPluginBase<FakeAccount>
    {
        public ScTaskViaUserService(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name)
                .Handle(ctx => ctx.Service.Create(new Entity("task") { ["subject"] = "user" }));
    }

    public sealed class ScAssociateOnAccount : EntityPluginBase<FakeAccount>
    {
        public ScAssociateOnAccount(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnAssociate().PostOperation().Sync().Handle(OnAssociated);
        private void OnAssociated(IAssociatePostContext<FakeAccount> ctx)
        {
            EntityReference target = (EntityReference)ctx.PluginExecutionContext.InputParameters["Target"];
            ctx.SystemService.Create(new Entity("vela_capture") { ["subject"] = $"associate:{target.LogicalName}:{target.Id}" });
        }
    }

    public sealed class ScCaptureGlobalContext : EntityPluginBase<FakeAccount>
    {
        public ScCaptureGlobalContext(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnAssociate().PreValidation().Sync().Handle(Capture);
            RegisterStep().OnAssociate().PostOperation().Sync().Handle(Capture);
            RegisterStep().OnAssociate().PostOperation().Async().Handle(Capture);
            RegisterStep().OnDisassociate().PreValidation().Sync().Handle(Capture);
            RegisterStep().OnDisassociate().PostOperation().Sync().Handle(Capture);
            RegisterStep().OnDisassociate().PostOperation().Async().Handle(Capture);
        }
        private void Capture(ILocalPluginContext ctx)
        {
            EntityReference target = (EntityReference)ctx.PluginExecutionContext.InputParameters["Target"];
            ctx.SystemService.Create(new Entity("vela_capture")
            {
                ["subject"] = $"{ctx.MessageName}:{ctx.Stage}:{ctx.Mode}:{ctx.PrimaryEntityName}:{ctx.PrimaryEntityId}:{target.LogicalName}:{target.Id}"
            });
        }
    }

    public sealed class ScCreatePost : EntityPluginBase<FakeAccount>
    {
        public ScCreatePost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class ScUpdatePost : EntityPluginBase<FakeAccount>
    {
        public ScUpdatePost(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(_ => { });
    }

    public sealed class ScCaptureUpdateId : EntityPluginBase<FakeAccount>
    {
        public ScCaptureUpdateId(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PreOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdate);
        private void OnUpdate(IUpdatePreContext<FakeAccount> ctx)
        {
            Entity target = (Entity)ctx.PluginExecutionContext.InputParameters["Target"];
            ctx.SystemService.Create(new Entity("vela_capture") { ["subject"] = $"update:{target.Id}:{ctx.PluginExecutionContext.PrimaryEntityId}" });
        }
    }

    public sealed class ScCaptureDeleteId : EntityPluginBase<FakeAccount>
    {
        public ScCaptureDeleteId(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnDelete().PreOperation().Sync().Handle(OnDelete);
        private void OnDelete(IDeletePreContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new Entity("vela_capture") { ["subject"] = $"delete:{ctx.PluginExecutionContext.PrimaryEntityId}" });
    }

    public sealed class ScSelfUpdateWithBypass : EntityPluginBase<FakeAccount>
    {
        public ScSelfUpdateWithBypass(string u, string s) : base(u, s) { }
        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdated);
        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx)
        {
            UpdateRequest request = new UpdateRequest { Target = new Entity("account", ctx.PluginExecutionContext.PrimaryEntityId) { ["name"] = "stamped" } };
            request["BypassCustomPluginExecution"] = true;
            ctx.Service.Execute(request);
        }
    }
}
