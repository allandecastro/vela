using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // PluginContext reads the plugin's registration: PrimaryEntityId follows the Target, the trigger parts a test leaves
    // unset come from the registered steps, a Custom API's request is validated like PluginPipeline does, and what the
    // context does not apply (FilterOn, registered images) is reported as a trace.
    public sealed class PluginContextRegistrationTests
    {
        // --- PrimaryEntityId ---

        [Fact]
        public void PrimaryEntityId_IsTheTargetId()
        {
            Guid id = Guid.NewGuid();

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().WithTarget(new FakeAccount { Id = id }).Build();

            Assert.Equal(id, ctx.PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_IsTheTargetReferenceId()
        {
            Guid id = Guid.NewGuid();

            LocalPluginContext ctx = PluginContext.ForMessage("vela_Touch")
                .WithInputParameter("Target", new EntityReference("account", id))
                .Build();

            Assert.Equal(id, ctx.PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_IsEmptyWithoutATarget()
        {
            LocalPluginContext ctx = PluginContext.For<FakeAccount>().OnCreate().Build();

            Assert.Equal(Guid.Empty, ctx.PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_IsEmptyOnAGlobalMessage()
        {
            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .OnMessage(PluginMessages.Associate)
                .WithInputParameter("Target", new EntityReference("account", Guid.NewGuid()))
                .Build();

            Assert.Equal(Guid.Empty, ctx.PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_SetWithWithPrimaryEntity_WinsOverTheTarget()
        {
            Guid stated = Guid.NewGuid();

            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .WithPrimaryEntity(FakeAccount.EntityLogicalNameValue, stated)
                .WithTarget(new FakeAccount { Id = Guid.NewGuid() })
                .Build();

            Assert.Equal(stated, ctx.PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_SetOnTheContext_IsKept()
        {
            Guid stated = Guid.NewGuid();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().WithTarget(new FakeAccount { Id = Guid.NewGuid() });
            builder.Context.PrimaryEntityId = stated;

            Assert.Equal(stated, builder.Build().PrimaryEntityId);
        }

        [Fact]
        public void PrimaryEntityId_FollowsATargetReplacedBetweenBuilds()
        {
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().WithTarget(new FakeAccount { Id = first });
            Assert.Equal(first, builder.Build().PrimaryEntityId);

            builder.WithTarget(new FakeAccount { Id = second });

            Assert.Equal(second, builder.Build().PrimaryEntityId);
        }

        // --- Trigger resolution ---

        [Fact]
        public void MessageOnly_OnASingleStepPlugin_TakesTheStageAndModeFromTheStep()
        {
            CreatePreOnlyPlugin plugin = new CreatePreOnlyPlugin();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().OnCreate().WithTarget(new FakeAccount());

            builder.Execute(plugin);

            Assert.Equal("pre", plugin.Ran);
            Assert.Equal((int)PluginStages.PreOperation, builder.Context.Stage);
            Assert.Equal((int)ExecutionMode.Synchronous, builder.Context.Mode);
        }

        [Fact]
        public void MessageOnly_OnSeveralSteps_ThrowsListingTheCandidates()
        {
            CreateTwoStagesPlugin plugin = new CreateTwoStagesPlugin();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().OnCreate().WithTarget(new FakeAccount()).Execute(plugin));

            Assert.Contains("several steps matching message 'Create', entity 'account'", ex.Message);
            Assert.Contains("Create/PreOperation/Sync on account [pre]", ex.Message);
            Assert.Contains("Create/PostOperation/Async on account [post]", ex.Message);
            Assert.Contains(".Stage(...) and .Sync()/.Async()", ex.Message);
            Assert.DoesNotContain(".OnCreate()", ex.Message);
            Assert.Null(plugin.Ran);
        }

        [Fact]
        public void MessageAndStage_PickTheStep_AndTheModeComesFromIt()
        {
            CreateTwoStagesPlugin plugin = new CreateTwoStagesPlugin();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .OnCreate().Stage(PluginStages.PostOperation).WithTarget(new FakeAccount());

            builder.Execute(plugin);

            Assert.Equal("post", plugin.Ran);
            Assert.Equal((int)ExecutionMode.Asynchronous, builder.Context.Mode);
        }

        [Fact]
        public void ModeOnly_PicksTheStep_AndTheMessageAndStageComeFromIt()
        {
            CreateTwoStagesPlugin plugin = new CreateTwoStagesPlugin();
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().Sync().WithTarget(new FakeAccount());

            builder.Execute(plugin);

            Assert.Equal("pre", plugin.Ran);
            Assert.Equal(PluginMessages.Create, builder.Context.MessageName);
            Assert.Equal((int)PluginStages.PreOperation, builder.Context.Stage);
        }

        [Fact]
        public void NoTrigger_OnSeveralSteps_ThrowsInsteadOfRunningUnderTheFakeDefaults()
        {
            CreateTwoStagesPlugin plugin = new CreateTwoStagesPlugin();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().WithTarget(new FakeAccount()).Execute(plugin));

            Assert.Contains("several steps matching entity 'account'", ex.Message);
            Assert.Contains(".OnCreate()/.OnUpdate()/.OnMessage(...) and .Stage(...) and .Sync()/.Async()", ex.Message);
            Assert.Null(plugin.Ran);
        }

        [Fact]
        public void AMessageThePluginDoesNotRegister_ThrowsNamingTheRegisteredSteps()
        {
            CreateTwoStagesPlugin plugin = new CreateTwoStagesPlugin();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().OnDelete().Execute(plugin));

            Assert.Equal(
                $"{typeof(CreateTwoStagesPlugin).FullName} registers no step matching message 'Delete', entity 'account'. Its registered " +
                "steps: Create/PreOperation/Sync on account [pre], Create/PostOperation/Async on account [post].",
                ex.Message);
        }

        [Fact]
        public void AnotherEntity_MatchesNoStep()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().WithPrimaryEntity("contact").OnCreate().Execute(new CreatePreOnlyPlugin()));

            Assert.Contains("no step matching message 'Create', entity 'contact'", ex.Message);
        }

        [Fact]
        public void AFullyStatedTrigger_IsLeftToTheDispatch_WhichNamesStageModeAndEntity()
        {
            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => PluginContext.For<FakeAccount>().OnCreate().Stage(PluginStages.PostOperation).Sync()
                    .WithTarget(new FakeAccount()).Execute(new CreatePreOnlyPlugin()));

            Assert.Equal(
                $"Plugin '{typeof(CreatePreOnlyPlugin).FullName}' has no registered step matching message 'Create', stage PostOperation, " +
                "mode Synchronous on entity 'account'.",
                ex.Message);
        }

        [Fact]
        public void AnInvalidRegistration_SurfacesItsReason_EvenWithAnExplicitTrigger()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => PluginContext.For<FakeAccount>().OnCreate().Stage(PluginStages.PreOperation).Sync()
                    .Execute(new BrokenRegistrationPlugin()));

            Assert.Equal($"Could not read the step registration of {typeof(BrokenRegistrationPlugin).FullName}: registration is broken", ex.Message);
        }

        // --- Custom API request validation ---

        [Fact]
        public void CustomApi_MissingRequiredInput_IsRejectedWithThePlatformFault()
        {
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => PluginContext.ForMessage("vela_Echo").Execute(new EchoApi(string.Empty, string.Empty)));

            Assert.Equal(unchecked((int)0x80040203), ex.Detail.ErrorCode);
            Assert.Equal("Required field 'Text' is missing for RequestName='vela_Echo'", ex.Detail.Message);
        }

        [Fact]
        public void CustomApi_InputOfTheWrongType_IsRejectedWithThePlatformFault()
        {
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => PluginContext.ForMessage("vela_Echo").WithInputParameter("Text", 42).Execute(new EchoApi(string.Empty, string.Empty)));

            Assert.Equal(unchecked((int)0x80040315), ex.Detail.ErrorCode);
        }

        [Fact]
        public void CustomApi_BoundWithoutTarget_IsRejected_AlsoOnBuild()
        {
            FaultException<OrganizationServiceFault> ex = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => PluginContext.ForMessage("vela_Touch").Build(new TouchApi(string.Empty, string.Empty)));

            Assert.Equal("Required field 'Target' is missing for RequestName='vela_Touch'", ex.Detail.Message);
        }

        [Fact]
        public void CustomApi_AValidRequest_Runs()
        {
            PluginContextBuilder<Entity> builder = PluginContext.ForMessage("vela_Echo").WithInputParameter("Text", "hi");

            builder.Execute(new EchoApi(string.Empty, string.Empty));

            Assert.Equal("echo:hi", builder.Context.OutputParameters["Echo"]);
        }

        [Fact]
        public void CustomApi_WithoutRequestValidation_RunsTheHandlerOnAnInvalidRequest()
        {
            PluginContextBuilder builder = PluginContext.ForMessage("vela_Echo");
            builder.WithoutRequestValidation();

            builder.Execute(new EchoApi(string.Empty, string.Empty));

            Assert.Equal("echo:", builder.Context.OutputParameters["Echo"]);
        }

        // --- What PluginContext does not apply, reported as a trace ---

        [Fact]
        public void FilterOn_ATargetWithNoneOfTheAttributes_TracesAWarning_AndStillRuns()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .WithTarget(new FakeAccount { NumberOfEmployees = 3 });

            RecordingPlugin plugin = builder.Execute<RecordingPlugin>();

            string warning = Assert.Single(builder.TraceCapture.Traces, t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal));
            Assert.Contains("filters on name and the Target holds none of them", warning);
            Assert.Null(plugin.LastName);
        }

        [Fact]
        public void FilterOn_ATargetWithAFilteredAttribute_TracesNoWarning()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().WithTarget(new FakeAccount { Name = "Zava" });

            builder.Execute<RecordingPlugin>();

            Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal));
        }

        [Fact]
        public void RegisteredImages_MissingFromTheContext_AreTraced()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>().WithTarget(new FakeAccount { Name = "Zava" });

            builder.Execute(new ImagesPlugin());

            string[] warnings = builder.TraceCapture.Traces.Where(t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, warnings.Length);
            Assert.Contains("registers the pre-image 'Before'", warnings[0]);
            Assert.Contains("registers the post-image 'After'", warnings[1]);
        }

        [Fact]
        public void RegisteredImages_PresentInTheContext_AreNotTraced()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .WithTarget(new FakeAccount { Name = "Zava" })
                .WithPreImage(new FakeAccount { Name = "Old" }, "Before")
                .WithPostImage(new FakeAccount { Name = "Zava" }, "After");

            builder.Execute(new ImagesPlugin());

            Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal));
        }

        [Fact]
        public void RegisteredImages_InTheBulkImageCollections_AreNotTraced()
        {
            EntityImageCollection images = new EntityImageCollection
            {
                ["Before"] = new FakeAccount { Name = "Old" },
                ["After"] = new FakeAccount { Name = "Zava" },
            };
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .WithTarget(new FakeAccount { Name = "Zava" })
                .WithBulkPreImages(images)
                .WithBulkPostImages(images);

            builder.Execute(new ImagesPlugin());

            Assert.DoesNotContain(builder.TraceCapture.Traces, t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal));
        }

        [Fact]
        public void StepsSharingTheTrigger_TheExecutingStepsFilterIsTheOneChecked()
        {
            PluginContextBuilder<FakeAccount> builder = PluginContext.For<FakeAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new FakeAccount { Name = "Zava" });
            builder.Context.OwningExtension = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid()) { Name = "headcount" };

            builder.Execute(new SharedTriggerPlugin());

            string warning = Assert.Single(builder.TraceCapture.Traces, t => t.StartsWith("Vela.Testing:", StringComparison.Ordinal));
            Assert.StartsWith("Vela.Testing: step 'headcount' filters on numberofemployees", warning);
        }

        private sealed class SharedTriggerPlugin() : EntityPluginBase<FakeAccount>(string.Empty, string.Empty)
        {
            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).WithStepKey("name").Handle(_ => { });
                RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.NumberOfEmployees).WithStepKey("headcount").Handle(_ => { });
            }
        }

        private sealed class CreatePreOnlyPlugin() : EntityPluginBase<FakeAccount>(string.Empty, string.Empty)
        {
            public string? Ran { get; private set; }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).Handle(_ => Ran = "pre");
        }

        private sealed class CreateTwoStagesPlugin() : EntityPluginBase<FakeAccount>(string.Empty, string.Empty)
        {
            public string? Ran { get; private set; }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().OnCreate().PreOperation().Sync().FilterOn(a => a.Name).WithStepKey("pre").Handle(_ => Ran = "pre");
                RegisterStep().OnCreate().PostOperation().Async().FilterOn(a => a.Name).WithStepKey("post").Handle(_ => Ran = "post");
            }
        }

        private sealed class BrokenRegistrationPlugin() : EntityPluginBase<FakeAccount>(string.Empty, string.Empty)
        {
            protected override void RegisterPluginSteps() => throw new InvalidOperationException("registration is broken");
        }

        private sealed class ImagesPlugin() : EntityPluginBase<FakeAccount>(string.Empty, string.Empty)
        {
            protected override void RegisterPluginSteps()
                => RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name)
                    .WithPreImage("Before", a => a.Name).WithPostImage("After", a => a.Name)
                    .Handle(_ => { });
        }
    }
}
