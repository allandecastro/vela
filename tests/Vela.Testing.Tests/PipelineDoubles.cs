using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.StepBuilder;

namespace Vela.Testing.Tests
{
    // Dedicated entity for the auto-discovery test, not touched by any other double.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class FakeAuto : Entity
    {
        public const string EntityLogicalNameValue = "vela_auto";

        public FakeAuto()
            : base(EntityLogicalNameValue)
        {
        }
    }

    // All pipeline doubles expose a (string, string) constructor so FrameworkManifestScanner discovers them.

    // Cascade A: on account create, provision a task.
    public sealed class CreateTaskOnAccountCreate : EntityPluginBase<FakeAccount>
    {
        public CreateTaskOnAccountCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
        {
            FakeAccount target = ctx.GetTarget<FakeAccount>();
            ctx.SystemService.Create(new FakeTask { Subject = $"Task for {target.Name}" });
        }
    }

    // Post-create typed handler that stamps ctx.Response.id onto a follow-up task's regarding lookup - proves the
    // harness surfaces the created id as OutputParameters["id"] (exactly as Dataverse does) so ctx.Response.id
    // resolves in a post-create handler, on both the crm.Create pipeline and the direct-execute paths.
    public sealed class StampResponseIdOnCreate : EntityPluginBase<FakeAccount>
    {
        public StampResponseIdOnCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Sync().FilteringAttributes(a => a.Name).Handler(OnCreated);

        private void OnCreated(ICreatePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new Entity("task")
            {
                ["subject"] = "follow-up",
                ["regardingobjectid"] = new EntityReference("account", ctx.Response.id),
            });
    }

    // Cascade B: on task create, create a note (proves a cascaded plugin fires from another plugin's write).
    public sealed class CreateNoteOnTaskCreate : EntityPluginBase<FakeTask>
    {
        public CreateNoteOnTaskCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Subject);

        protected override void Execute(ILocalPluginContext ctx)
            => ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "auto-note" });
    }

    // Fires on task delete — proves a cascaded child delete runs the child's Delete plugins (firing is observable
    // via the pipeline ExecutionLog).
    public sealed class OnTaskDelete : EntityPluginBase<FakeTask>
    {
        public OnTaskDelete(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous);

        protected override void Execute(ILocalPluginContext ctx)
        {
        }
    }

    // Fires only when 'name' changes (filtering attributes).
    public sealed class OnNameChange : EntityPluginBase<FakeAccount>
    {
        public OnNameChange(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
        {
            // Firing is observable via the pipeline ExecutionLog; no side effect needed.
        }
    }

    // Update step with an explicit, stable key, for bypass-by-step-id tests.
    public sealed class KeyedNameChange : EntityPluginBase<FakeAccount>
    {
        public const string StepKey = "keyed-name-change";

        public KeyedNameChange(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Key(StepKey);

        protected override void Execute(ILocalPluginContext ctx)
        {
            // Firing is observable via the pipeline ExecutionLog.
        }
    }

    // Fires on name OR ownerid changes; tags a task with which split-pass it ran in (Target carries only the
    // standard fields on the first pass and only the owner/state fields on the second).
    public sealed class RecordUpdatePasses : EntityPluginBase<FakeOwnable>
    {
        public RecordUpdatePasses(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name, a => a.OwnerId);

        protected override void Execute(ILocalPluginContext ctx)
        {
            FakeOwnable target = ctx.GetTarget<FakeOwnable>();
            ctx.SystemService.Create(new FakeTask { Subject = target.Contains("ownerid") ? "pass:owner" : "pass:standard" });
        }
    }

    // Fires when statecode changes — used to prove SetState routes through the Update pipeline.
    public sealed class OnStateChange : EntityPluginBase<FakeOwnable>
    {
        public OnStateChange(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.StateCode);

        protected override void Execute(ILocalPluginContext ctx)
        {
            // Firing is observable via the pipeline ExecutionLog.
        }
    }

    // Reads pre/post images and records them on a task.
    public sealed class CaptureImages : EntityPluginBase<FakeAccount>
    {
        public CaptureImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage().Attributes(a => a.Name)
                .PostImage().Attributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
        {
            Entity pre = ctx.PluginExecutionContext.PreEntityImages[PluginImageAliases.PreImage];
            Entity post = ctx.PluginExecutionContext.PostEntityImages[PluginImageAliases.PostImage];
            ctx.SystemService.Create(new FakeTask
            {
                Subject = $"{pre.GetAttributeValue<string>("name")}->{post.GetAttributeValue<string>("name")}"
            });
        }
    }

    // Updates the same record on update -> recursion until the depth guard trips.
    public sealed class RecurseOnUpdate : EntityPluginBase<FakeAccount>
    {
        public RecurseOnUpdate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
        {
            FakeAccount target = ctx.GetTarget<FakeAccount>();
            ctx.SystemService.Update(new FakeAccount { Id = target.Id, Name = "again" });
        }
    }

    // Throws after the write to exercise rollback.
    public sealed class ThrowOnPostCreate : EntityPluginBase<FakeAccount>
    {
        public ThrowOnPostCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
            => throw new PluginBusinessException("rejected after write");
    }

    // Asynchronous step that throws: its failure must be recorded, not surfaced to the triggering caller.
    public sealed class AsyncThrowOnCreate : EntityPluginBase<FakeAccount>
    {
        public AsyncThrowOnCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Asynchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
            => throw new PluginBusinessException("async boom");
    }

    // Asynchronous step: creates a note after the synchronous commit.
    public sealed class AsyncCreateNote : EntityPluginBase<FakeAccount>
    {
        public AsyncCreateNote(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Asynchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
            => ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "async-note" });
    }

    // Discovered automatically by FromAssembly; operates on a dedicated entity.
    public sealed class AutoDiscoveredPlugin : EntityPluginBase<FakeAuto>
    {
        public AutoDiscoveredPlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Id);

        protected override void Execute(ILocalPluginContext ctx)
        {
            // Firing is observable via the pipeline ExecutionLog.
        }
    }

    // Bulk create step: fires once per CreateMultiple (or single Create, merged) with the whole Targets
    // collection; records the batch size it observed via ctx.BulkTargets.
    public sealed class OnAccountCreateMultiple : EntityPluginBase<FakeAccount>
    {
        public OnAccountCreateMultiple(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnCreateMultiple().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
            => ctx.SystemService.Create(new FakeTask { Subject = $"bulk:{ctx.BulkTargets?.Entities.Count ?? 0}" });
    }

    // Bulk update step: reads per-row pre/post image collections (aligned with Targets by index).
    public sealed class CaptureBulkImages : EntityPluginBase<FakeAccount>
    {
        public CaptureBulkImages(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdateMultiple().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage().Attributes(a => a.Name)
                .PostImage().Attributes(a => a.Name);

        protected override void Execute(ILocalPluginContext ctx)
        {
            EntityCollection targets = ctx.BulkTargets!;
            for (int i = 0; i < targets.Entities.Count; i++)
            {
                Entity pre = ctx.BulkPreEntityImagesCollection[i][PluginImageAliases.PreImage];
                Entity post = ctx.BulkPostEntityImagesCollection[i][PluginImageAliases.PostImage];
                ctx.SystemService.Create(new FakeTask
                {
                    Subject = $"{pre.GetAttributeValue<string>("name")}->{post.GetAttributeValue<string>("name")}"
                });
            }
        }
    }

    // Associate step (Target carries an EntityReference, plus Relationship/RelatedEntities extra inputs).
    public sealed class OnAccountAssociate : EntityPluginBase<FakeAccount>
    {
        public OnAccountAssociate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnAssociate().PostOperation().Mode(ExecutionMode.Synchronous);

        protected override void Execute(ILocalPluginContext ctx)
        {
            // Firing (and the EntityReference target / extra inputs it carries) is observable via ExecutionLog.
        }
    }

    // Asynchronous disassociate step: creates a note after the synchronous commit.
    public sealed class OnAccountDisassociateAsync : EntityPluginBase<FakeAccount>
    {
        public OnAccountDisassociateAsync(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnDisassociate().PostOperation().Mode(ExecutionMode.Asynchronous);

        protected override void Execute(ILocalPluginContext ctx)
            => ctx.SystemService.Create(new Entity("annotation") { ["subject"] = "disassociated" });
    }

    // Update step whose pre-image requests no specific attributes -> the image carries every column.
    public sealed class CaptureFullPreImage : EntityPluginBase<FakeAccount>
    {
        public CaptureFullPreImage(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .PreImage(); // no Attributes(...) -> the projected image keeps all columns

        protected override void Execute(ILocalPluginContext ctx)
        {
            Entity pre = ctx.PluginExecutionContext.PreEntityImages[PluginImageAliases.PreImage];
            ctx.SystemService.Create(new FakeTask { Subject = pre.GetAttributeValue<string>("name") ?? "?" });
        }
    }

    // Fires on the SendEmail message (a modelled message with no data simulation): proves the pipeline fires
    // registered steps for these messages like any other plugin, and that ctx.Request is the typed SendEmailRequest.
    public sealed class OnAccountEmailSent : EntityPluginBase<FakeAccount>
    {
        public OnAccountEmailSent(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnSendEmail().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnSent);

        private void OnSent(ISendEmailPostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new Entity("task") { ["subject"] = $"sent:{ctx.Request.EmailId}" });
    }

    // Fires on AddToQueue, whose request carries a Target EntityReference (so the primary entity is derived from it).
    public sealed class OnAccountAddedToQueue : EntityPluginBase<FakeAccount>
    {
        public OnAccountAddedToQueue(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterSteps().OnAddToQueue().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnQueued);

        private void OnQueued(IAddToQueuePostContext<FakeAccount> ctx)
            => ctx.SystemService.Create(new Entity("task") { ["subject"] = $"queue:{ctx.Request.Target.Id}:{ctx.Request.DestinationQueueId}" });
    }

    // Lightweight Custom API for the Execute() path.
    public sealed class GreetApi : CustomApiBase
    {
        public GreetApi(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_Greet", nameof(Handle));

        private void Handle(ILocalPluginContext ctx)
        {
            string? name = ctx.GetInputParameter<string>("Name");
            ctx.SetOutputParameter("Message", $"Hello {name}");
        }
    }
}
