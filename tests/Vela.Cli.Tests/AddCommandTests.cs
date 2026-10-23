using System;
using System.IO;
using System.Linq;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class AddCommandTests
{
    [Fact]
    public void Run_RejectsUnknownOption_BeforeDispatch()
    {
        Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "Foo", "--entty", "Account"]));
    }

    [Fact]
    public void Run_HelpAnywhere_PrintsUsage_AndSucceeds()
    {
        Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--help"]));
    }

    [Fact]
    public void BuildAddReport_Lists_Description_And_Created_Files()
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
            AddCommand.BuildAddReport("plugin Foo (Create Account)", ["/proj/Account/Plugins/Foo.cs"]));

        Assert.Equal("plugin Foo (Create Account)", doc.RootElement.GetProperty("description").GetString());
        string? created = Assert.Single(System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Select(doc.RootElement.GetProperty("created").EnumerateArray(), e => e.GetString())));
        Assert.Equal("/proj/Account/Plugins/Foo.cs", created);
    }

    [Fact]
    public void Add_Plugin_GeneratesTypedPlugin_WithInferredNamespaces()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountValidatorPlugin", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Account", "Plugins", "AccountValidatorPlugin.cs");
            Assert.True(File.Exists(file));

            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Account.Plugins", content);
            Assert.Contains("using Acme.Shared.Models.Generated;", content);
            Assert.Contains("public sealed partial class AccountValidatorPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)", content);
            Assert.Contains("RegisterStep().OnUpdate()", content);
        });
    }

    [Theory]
    [InlineData("Associate", "RegisterStep().OnAssociate()")]
    [InlineData("retrievemultiple", "RegisterStep().OnRetrieveMultiple()")]
    [InlineData("CreateMultiple", "RegisterStep().OnCreateMultiple()")]
    [InlineData("UpdateMultiple", "RegisterStep().OnUpdateMultiple()")]
    public void Add_Plugin_KnownMessage_RendersTypedCall(string message, string expectedCall)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string[] filter = AddCommand.RequiresFilter(message) ? ["--filtering-attributes", "Name"] : []; // FilterOn exists only on these
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, .. filter, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains(expectedCall, content);
        });
    }

    // --message accepts the sdkmessage a step registers on as well as the request name: Send is OnSendEmail(), and
    // Win, shared by two requests, resolves through the table (WinQuote on quote, WinOpportunity on opportunity).
    [Theory]
    [InlineData("SendEmail", "Email", "SendEmail")]
    [InlineData("Send", "Email", "SendEmail")]
    [InlineData("deliverincoming", "Email", "DeliverIncomingEmail")]
    [InlineData("Win", "Quote", "WinQuote")]
    [InlineData("Win", "Opportunity", "WinOpportunity")]
    [InlineData("Close", "Incident", "CloseIncident")]
    [InlineData("Send", "Fax", "SendFax")]
    [InlineData("Send", "Template", "SendTemplate")]
    [InlineData("AddItem", "Campaign", "AddItemCampaign")]
    [InlineData("RemoveItem", "Campaign", "RemoveItemCampaign")]
    [InlineData("LockSalesOrderPricing", "SalesOrder", "LockSalesOrderPricing")]
    public void ResolveKnownMessage_maps_an_sdkmessage_to_its_typed_trigger(string message, string entity, string expected)
        => Assert.Equal(expected, AddCommand.ResolveKnownMessage(message, entity));

    [Theory]
    [InlineData("Win", "Account")]          // Win runs on opportunity or quote only
    [InlineData("Send", "Account")]         // Send runs on email, fax or template only
    [InlineData("Renew", "Contract")]       // no custom step is possible on Renew (RenewContract)
    [InlineData("acme_DoThing", "Account")] // a custom message
    [InlineData("Revise", "Quote")]         // no custom step is possible on Revise, so it has no typed trigger
    public void ResolveKnownMessage_returns_null_without_a_typed_trigger(string message, string entity)
        => Assert.Null(AddCommand.ResolveKnownMessage(message, entity));

    [Fact]
    public void Add_Plugin_CustomMessage_RendersRequestTypedTrigger()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountCustom", "--entity", "Account", "--message", "acme_DoThing", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            // No magic string: the step registers through the request `vela models --custom-apis/--messages`
            // generates, and the handler gets its request-typed context.
            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountCustom.cs"));
            Assert.Contains("RegisterStep().On<acme_DoThingRequest>()", content);
            Assert.Contains("private void Handleacme_DoThing(Handleacme_DoThingContext ctx)", content);
            Assert.DoesNotContain("IMessagePostContext", content);
            Assert.DoesNotContain("OnMessage(", content);
        });
    }

    // Every message `add plugin` registers (each typed trigger, an SDK request, a generated request) at every stage
    // binds the handler to its generated Handle{Message}Context in a partial class, never the message interface. The
    // build smoke test (ProjectScaffolderTests.Generate_ThenDotnetBuild_Succeeds_Minimal) compiles these forms.
    public static TheoryData<string, string> EveryMessageAndStage()
    {
        TheoryData<string, string> data = [];
        foreach (string message in AddCommand.KnownMessages.Concat(["SetState", "acme_DoThing"]))
        {
            foreach (string stage in new[] { "PreValidation", "PreOperation", "PostOperation" })
            {
                data.Add(message, stage);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryMessageAndStage))]
    public void Add_Plugin_BindsTheGeneratedHandlerContext_ForEveryMessageAndStage(string message, string stage)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string[] filter = AddCommand.RequiresFilter(message) ? ["--filtering-attributes", "Name"] : [];
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, "--stage", stage, .. filter, "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains("public sealed partial class AccountStep(", content);
            Assert.Contains($"                .Handle(Handle{message});", content);
            Assert.Contains($"private void Handle{message}(Handle{message}Context ctx)", content);
            Assert.DoesNotMatch(@"I\w*(Pre|Post)Context<", content);
        });
    }

    [Theory]
    [InlineData("Delete", "OnDelete()", "HandleDelete")]
    [InlineData("SetState", "On<Microsoft.Crm.Sdk.Messages.SetStateRequest>()", "HandleSetState")]
    [InlineData("acme_DoThing", "On<acme_DoThingRequest>()", "HandleAcme_DoThing")]
    public void Add_Step_BindsTheGeneratedHandlerContext_AndMakesThePluginPartial(string message, string trigger, string handler)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string pluginPath = Path.Combine(dir, "Account", "Plugins", "AccountRouter.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            File.WriteAllText(pluginPath, HandlerBasedPlugin);

            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--message", message, "--handler", handler, "--stage", "PreOperation", "--project", project]));

            string content = File.ReadAllText(pluginPath);
            Assert.Contains($"RegisterStep().{trigger}", content);
            Assert.Contains($"private void {handler}({handler}Context ctx)", content);
            Assert.Contains("public sealed partial class AccountRouter : EntityPluginBase<Account>", content);
            // The existing handler keeps its parameter type.
            Assert.Contains("private void HandleCreate(ILocalPluginContext ctx)", content);
        });
    }

    [Fact]
    public void Add_Plugin_PreOperationStage_RendersPreStageAndPreContext()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountPre", "--entity", "Account", "--message", "Delete", "--stage", "PreOperation", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountPre.cs"));
            Assert.Contains(".PreOperation()", content);
            Assert.Contains("private void HandleDelete(HandleDeleteContext ctx)", content);
        });
    }

    [Fact]
    public void Add_Plugin_AsyncMode_RendersAsyncCall()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountAsync", "--entity", "Account", "--message", "Delete", "--mode", "Async", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountAsync.cs"));
            Assert.Contains(".Async()", content);
        });
    }

    [Fact]
    public void Add_Plugin_AsyncPreStage_FailsClearly()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountBad", "--entity", "Account", "--message", "Delete", "--stage", "PreOperation", "--mode", "Async", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Add_Plugin_InvalidStage_FailsClearly()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountBadStage", "--entity", "Account", "--message", "Delete", "--stage", "whenever", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Add_Step_ExplicitFilteringAttributes_RendersFilterOn()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRouter", "--entity", "Account", "--message", "Delete", "--project", project]));
            int exit = AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountRouter.cs"));
            Assert.Contains(".FilterOn(e => e.Name)", content);
            Assert.DoesNotContain("// TODO: add .FilterOn", content);
        });
    }

    [Fact]
    public void Filtering_attributes_on_a_message_without_FilterOn_are_rejected()
    {
        // A Delete config has no FilterOn: emitting one would write a plugin that does not compile.
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "AccountDelete", "--entity", "Account", "--message", "Delete", "--filtering-attributes", "Name", "--project", project]));
            Assert.False(File.Exists(Path.Combine(dir, "Account", "Plugins", "AccountDelete.cs")));

            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRouter3", "--entity", "Account", "--message", "Delete", "--project", project]));
            string path = Path.Combine(dir, "Account", "Plugins", "AccountRouter3.cs");
            string before = File.ReadAllText(path);
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["step", "AccountRouter3", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete2", "--filtering-attributes", "Name", "--project", project]));
            Assert.Equal(before, File.ReadAllText(path));
        });
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("CREATE", true)]
    [InlineData("createmultiple", true)]
    [InlineData("Delete", false)]
    [InlineData("acme_DoThing", false)]
    public void RequiresFilter_matches_the_message_case_insensitively(string message, bool expected)
    {
        // The wizard passes the message as typed; the CLI accepts any casing, so the rule must too.
        Assert.Equal(expected, AddCommand.RequiresFilter(message));
    }

    [Fact]
    public void Add_Step_NoFilteringAttributes_FailsAndLeavesThePluginUntouched()
    {
        // An Update step must FilterOn; without --filtering-attributes the step would not build at runtime.
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRouter2", "--entity", "Account", "--message", "Delete", "--project", project]));
            string path = Path.Combine(dir, "Account", "Plugins", "AccountRouter2.cs");
            string before = File.ReadAllText(path);

            int exit = AddCommand.Run(["step", "AccountRouter2", "--entity", "Account", "--message", "Update", "--project", project]);

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Equal(before, File.ReadAllText(path));
        });
    }

    // CreateMultiple/UpdateMultiple map to the typed OnCreateMultiple()/OnUpdateMultiple() builder methods,
    // which require a filtering attribute at compile time - so the generator must also emit the filtering call,
    // otherwise the scaffolded plugin would not compile.
    [Theory]
    [InlineData("CreateMultiple", "RegisterStep().OnCreateMultiple()")]
    [InlineData("UpdateMultiple", "RegisterStep().OnUpdateMultiple()")]
    public void Add_Plugin_BulkMessage_RendersTypedCallWithFiltering(string message, string expectedCall)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountBulk", "--entity", "Account", "--message", message, "--filtering-attributes", "Name", "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountBulk.cs"));
            Assert.Contains(expectedCall, content);
            Assert.Contains(".FilterOn(", content);
        });
    }

    // E2: --filtering-attributes renders one lambda per column, in order.
    [Fact]
    public void Add_Plugin_Update_ExplicitFilteringAttributes_RendersLambdas()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountFiltered", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name,Telephone1", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountFiltered.cs"));
            Assert.Contains(".FilterOn(e => e.Name, e => e.Telephone1)", content);
        });
    }

    // A filtering-required message with no explicit attributes fails with a clear error and writes nothing, instead
    // of emitting a plugin whose step fails to build at runtime.
    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    public void Add_Plugin_FilteredMessage_NoFiltering_FailsAndWritesNothing(string message)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountNoFilter", "--entity", "Account", "--message", message, "--project", project]);

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.False(File.Exists(Path.Combine(dir, "Account", "Plugins", "AccountNoFilter.cs")));
        });
    }

    // With no explicit attributes, the generator does NOT guess a column from the model (a deliberate
    // restraint, matching 'add step'): it leaves a TODO instead, even when a model is present.
    [Fact]
    public void Add_Plugin_Update_NoFiltering_DoesNotGuessFromModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Demo.sln"), string.Empty);
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            Directory.CreateDirectory(pluginsDir);
            string project = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");

            string modelsDir = Path.Combine(root, "src", "Acme.Shared", "Models", "Generated");
            Directory.CreateDirectory(modelsDir);
            File.WriteAllText(Path.Combine(modelsDir, "Account.cs"),
                "public sealed class Account : Entity\n{\n    [AttributeLogicalName(\"accountnumber\")]\n    public string? AccountNumber\n    {\n        get; set;\n    }\n\n    [AttributeLogicalName(\"name\")]\n    public string? Name\n    {\n        get; set;\n    }\n}\n");

            int exit = AddCommand.Run(["plugin", "AccountAuto", "--entity", "Account", "--message", "Update", "--project", project]);

            // Despite a model with a Name column, no column is auto-selected: the caller must name it.
            Assert.Equal(ExitInvalidArguments, exit);
            Assert.False(File.Exists(Path.Combine(pluginsDir, "Account", "Plugins", "AccountAuto.cs")));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    // A Custom API's unique name must carry the target solution's publisher prefix, which is not the .NET
    // project prefix. --prefix overrides the default so the generated unique name matches the publisher.
    [Fact]
    public void Add_CustomApi_PrefixOverride_UsesPublisherPrefixInUniqueName()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["customapi", "RecalculateTotals", "--prefix", "dv", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "RecalculateTotals.cs"));
            Assert.Contains("RegisterCustomApi(\"dv_RecalculateTotals\", Handle)", content);
            Assert.DoesNotContain("acme_RecalculateTotals", content);
        });
    }

    [Theory]
    [InlineData("1bad")]       // starts with a digit
    [InlineData("x")]          // too short (min 2)
    [InlineData("toolongpx")]  // 9 chars (max 8)
    [InlineData("has_us")]     // not alphanumeric
    public void Add_CustomApi_InvalidPrefix_Fails(string prefix)
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["customapi", "Foo", "--prefix", prefix, "--project", project]));
        });
    }

    // SetState/SetStateDynamicEntity are deprecated by the platform (use an Update of statecode/statuscode),
    // so the builder exposes no curated On...() method for them. `vela add` registers them like any other
    // message, through the request type: the SDK class, fully qualified, when one ships (SetStateRequest), else
    // the request `vela models --messages` generates.
    [Theory]
    [InlineData("SetState", "Microsoft.Crm.Sdk.Messages.SetStateRequest")]
    [InlineData("setstate", "Microsoft.Crm.Sdk.Messages.SetStateRequest")]
    // An sdkmessage other requests run as maps to the request on the entity's table (PR #480 review): there is no
    // SetStateDynamicEntityRequest nor CloseRequest, so neither is synthesized.
    [InlineData("SetStateDynamicEntity", "Microsoft.Crm.Sdk.Messages.SetStateRequest")]
    [InlineData("Close", "Microsoft.Crm.Sdk.Messages.CloseQuoteRequest", "Quote")]
    [InlineData("Publish", "Microsoft.Crm.Sdk.Messages.PublishDuplicateRuleRequest", "DuplicateRule")]
    [InlineData("Publish", "Microsoft.Crm.Sdk.Messages.PublishXmlRequest")]
    public void Add_Plugin_UnmodelledMessage_RendersRequestTypedTrigger(string message, string requestType, string entity = "Account")
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", entity, "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, entity, "Plugins", "AccountStep.cs"));
            Assert.Contains($"RegisterStep().On<{requestType}>()", content);
            Assert.Matches(@"private void (Handle\w+)\(\1Context ctx\)", content);
        });
    }

    [Theory]   // PR #480 review
    [InlineData("Close", "Account", "'Close' runs only as CloseIncident on incident, CloseQuote on quote, none of them on Account.")]
    [InlineData("Execute", "Account", "'Execute' is the sdkmessage of several requests (ExecuteCampaignActivity, ExecuteFetch).")]
    public void ResolveRequestName_rejects_an_sdkmessage_with_no_single_request_for_the_table(string message, string entity, string expected)
    {
        Assert.Null(AddCommand.ResolveRequestName(message, entity, out string error));
        Assert.StartsWith(expected, error);
    }

    [Theory]
    [InlineData("Close", "Quote", "CloseQuote")]
    [InlineData("Close", "Incident", "CloseIncident")]
    [InlineData("SetStateDynamicEntity", "Account", "SetState")]
    [InlineData("CloseQuote", "Quote", "CloseQuote")]
    [InlineData("acme_DoThing", "Account", "acme_DoThing")]
    public void ResolveRequestName_maps_an_sdkmessage_to_the_request_on_the_table(string message, string entity, string expected)
        => Assert.Equal(expected, AddCommand.ResolveRequestName(message, entity, out _));

    [Fact]
    public void Add_Plugin_fails_on_an_sdkmessage_that_does_not_run_on_the_entity()   // PR #480 review
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", "Close", "--project", project]));
            Assert.False(File.Exists(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs")));
        });
    }

    [Fact]
    public void ResolveKnownMessage_does_not_map_close_on_quote_to_the_incident_trigger()   // PR #480 review
        => Assert.Null(AddCommand.ResolveKnownMessage("Close", "Quote"));

    // Every message with a typed builder method (including the Tier-2 Merge / sharing messages) renders the
    // strongly-typed On...() call rather than the generic request-typed On<TRequest>().
    [Theory]
    [InlineData("Assign")]
    [InlineData("Merge")]
    [InlineData("GrantAccess")]
    [InlineData("ModifyAccess")]
    [InlineData("RevokeAccess")]
    [InlineData("Delete")]
    [InlineData("Reschedule")]
    [InlineData("UnlockSalesOrderPricing")]
    public void Add_Plugin_KnownMessage_RendersTypedOnCall(string message)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains($"RegisterStep().On{message}()", content);
            Assert.DoesNotContain("On<", content);
        });
    }

    [Fact]
    public void Add_Plugin_WithoutEntity_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["plugin", "OrphanPlugin", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Add_GlobalCustomApi_GeneratesLightweightApi_WithDerivedUniqueName()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["customapi", "RecalculateTotals", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "GlobalCustomApis", "RecalculateTotals.cs");
            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.GlobalCustomApis", content);
            Assert.Contains("public sealed class RecalculateTotals(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)", content);
            Assert.Contains("RegisterCustomApi(\"acme_RecalculateTotals\", Handle)", content);
        });
    }

    [Fact]
    public void Add_BoundCustomApi_GeneratesTypedApi_BoundToEntity()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["customapi", "ExportContact", "--entity", "Contact", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Contact", "CustomApis", "ExportContact.cs");
            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Contact.CustomApis", content);
            Assert.Contains("CustomApiBase<ExportContactRequest, ExportContactResponse>", content);
            Assert.Contains(".BoundTo<Contact>()", content);
            Assert.Contains("RegisterCustomApi(\"acme_ExportContact\")", content);
        });
    }

    [Fact]
    public void Add_Service_GeneratesTableScopedInterfaceAndImplementation()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["service", "AccountFollowUpService", "--entity", "Account", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Account", "Services", "AccountFollowUpService.cs");
            Assert.True(File.Exists(file));

            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Account.Services", content);
            Assert.Contains("using Acme.Shared.Models.Generated;", content);
            Assert.Contains("public interface IAccountFollowUpService", content);
            Assert.Contains("public sealed class AccountFollowUpService(IOrganizationService service) : IAccountFollowUpService", content);
            Assert.Contains("private readonly IOrganizationService _service = service;", content);
            Assert.Contains("void Execute(Account target)", content);
        });
    }

    [Fact]
    public void Add_Service_WithoutEntity_GeneratesASharedServiceOnTheLateBoundEntity()
    {
        // A service used by the plugins of several tables: no table folder, no early-bound type.
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["service", "AuditService", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Services", "AuditService.cs");
            Assert.True(File.Exists(file));

            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Services", content);
            Assert.DoesNotContain("Models.Generated", content);
            Assert.Contains("public sealed class AuditService(IOrganizationService service) : IAuditService", content);
            Assert.Contains("void Execute(Entity target)", content);
            Assert.Contains("lives under <c>Services/</c>", content);
        });
    }

    [Fact]
    public void Add_Plugin_And_Step_GenerateLfOnlyFiles()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountEol", "--entity", "Account", "--message", "Delete", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountEol", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project]));

            // Generated files must use a single line ending (LF). On a CRLF checkout the raw-string templates
            // and the injected ("\n") step would otherwise mix endings.
            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountEol.cs"));
            Assert.DoesNotContain("\r\n", content);
            Assert.DoesNotContain("\r", content);
        });
    }

    [Fact]
    public void Add_CustomApi_Function_RendersAsFunction()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "GetScore", "--entity", "Account", "--function", "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "CustomApis", "GetScore.cs"));
            Assert.Contains(".AsFunction()", content);
            Assert.DoesNotContain(".AsAction()", content);
        });
    }

    // A function must declare at least one response property or Build() rejects it (and Dataverse would not expose
    // it): the generated function carries a placeholder Result, an action does not. The build smoke test
    // (ProjectScaffolderTests.Generate_ThenDotnetBuild_Succeeds) compiles and scans both function forms.
    [Fact]
    public void Add_CustomApi_Function_DeclaresAPlaceholderResponseProperty()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "GetScore", "--entity", "Account", "--function", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Ping", "--global", "--function", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "DoThing", "--global", "--project", project]));

            string bound = File.ReadAllText(Path.Combine(dir, "Account", "CustomApis", "GetScore.cs"));
            Assert.Contains("        public string Result\n        {\n            get => Get<string>();\n            set => Set(value);\n        }", bound.Replace("\r\n", "\n"));

            string global = File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "Ping.cs")).Replace("\r\n", "\n");
            Assert.Contains(".AsFunction()\n                .ResponseParameter<string>(\"Result\", \"TODO: rename or replace; a function must return at least one value.\");", global);
            Assert.Contains("ctx.SetOutputParameter(\"Result\", string.Empty);", global);

            string action = File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "DoThing.cs"));
            Assert.DoesNotContain("Result", action);
            Assert.Contains(".AsAction();", action);
        });
    }

    [Fact]
    public void Add_CustomApi_DefaultsToAction()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "DoThing", "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "DoThing.cs"));
            Assert.Contains(".AsAction()", content);
        });
    }

    [Fact]
    public void Add_CustomApi_Global_ForcesUnboundEvenIsAllowed()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Ping", "--global", "--project", project]));
            Assert.True(File.Exists(Path.Combine(dir, "GlobalCustomApis", "Ping.cs")));
        });
    }

    [Fact]
    public void Add_CustomApi_Bound_WithoutEntity_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["customapi", "X", "--bound", "--project", project])));
    }

    [Fact]
    public void Add_CustomApi_Global_WithEntity_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["customapi", "X", "--global", "--entity", "Account", "--project", project])));
    }

    [Fact]
    public void Add_CustomApi_FunctionAndAction_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["customapi", "X", "--function", "--action", "--project", project])));
    }

    [Fact]
    public void Add_UnknownKind_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["widget", "Foo", "--project", project]));
        });
    }

    [Fact]
    public void Add_ExistingFile_Fails_WithoutForce_AndOverwrites_WithForce()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--filtering-attributes", "Name", "--project", project]));
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--filtering-attributes", "Name", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--filtering-attributes", "Name", "--project", project, "--force"]));
        });
    }

    [Fact]
    public void Add_NoProject_AndNoCsprojInCwd_Fails()
    {
        RunInProject("Acme.Plugins", (_, dir) =>
        {
            string empty = Path.Combine(dir, "empty");
            Directory.CreateDirectory(empty);
            // --project points at a directory with no .csproj.
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "X", "--entity", "Account", "--project", empty]));
        });
    }

    [Fact]
    public void Add_Step_AppendsRegistrationAndHandler_ToExistingMultiStepPlugin()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string pluginPath = Path.Combine(dir, "Account", "Plugins", "AccountRouter.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            File.WriteAllText(pluginPath, HandlerBasedPlugin);

            int exit = AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(pluginPath);
            Assert.Contains("RegisterStep().OnUpdate()", content);
            Assert.Contains(".Handle(HandleUpdate);", content);
            Assert.Contains("private void HandleUpdate(HandleUpdateContext ctx)", content);
            // The generated context is nested in the plugin class, so add step makes the class partial.
            Assert.Contains("public sealed partial class AccountRouter : EntityPluginBase<Account>", content);
            // The original step is preserved.
            Assert.Contains(".Handle(HandleCreate);", content);
        });
    }

    [Fact]
    public void Add_Step_MissingPluginFile_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["step", "DoesNotExist", "--entity", "Account", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    private const string HandlerBasedPlugin = """
namespace Acme.Plugins.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using Vela.Steps;
    using Acme.Shared.Models.Generated;

    public sealed class AccountRouter : EntityPluginBase<Account>
    {
        public AccountRouter(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .Handle(HandleCreate);
        }

        private void HandleCreate(ILocalPluginContext ctx)
        {
        }
    }
}
""";

    // Creates a temp directory containing a minimal .csproj with the given RootNamespace, then invokes the action
    // with the project file path and its directory.
    [Fact]
    public void Add_Plugin_Assign_UsesTheTypedTrigger()   // gap: OnMessage("Assign") tripped VELA006
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountAssign", "--entity", "Account", "--message", "assign", "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountAssign.cs"));
            Assert.Contains("RegisterStep().OnAssign()", content);
            Assert.Contains("private void HandleAssign(HandleAssignContext ctx)", content);
            Assert.DoesNotContain("OnMessage(", content);
        });
    }

    [Fact]
    public void KnownMessages_CoverEveryTypedTriggerInStepApi()
    {
        // Every parameterless On<Message>() trigger the step builder exposes must be in KnownMessages; otherwise
        // `vela add` falls back to OnMessage("<Message>"), which VELA006 flags.
        string? dir = AppContext.BaseDirectory;
        string? stepApi = null;
        for (int i = 0; i < 12 && dir is not null && stepApi is null; i++)
        {
            string candidate = Path.Combine(dir, "src", "Vela.Plugins", "Steps", "StepApi.cs");
            stepApi = File.Exists(candidate) ? candidate : null;
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(stepApi);
        string[] typed = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(stepApi!), @"\bOn(?<m>[A-Z][A-Za-z]*)\(\)")
            .Select(m => m.Groups["m"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(typed);
        Assert.Equal(typed, AddCommand.KnownMessages.OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("service", "--message", "Update")]
    [InlineData("service", "--prefix", "acme")]
    [InlineData("plugin", "--prefix", "acme")]
    [InlineData("plugin", "--function", null)]
    [InlineData("customapi", "--message", "Update")]
    [InlineData("customapi", "--filtering-attributes", "Name")]
    [InlineData("step", "--force", null)]
    [InlineData("step", "--kind", "plugin")]
    [InlineData("test", "--stage", "PreOperation")]
    public void Add_RejectsAnOptionTheKindDoesNotUse(string kind, string option, string? value)   // A-78
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            string[] args = value is null
                ? [kind, "Thing", "--entity", "Account", option, "--project", project]
                : [kind, "Thing", "--entity", "Account", option, value, "--project", project];

            Assert.Equal(ExitInvalidArguments, AddCommand.Run(args));
            Assert.Empty(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Add_CustomApi_UsesThePublisherPrefixRecordedInTheProject()   // A-50
    {
        RunInProject("Zava.Sales.Plugins", (project, dir) =>
        {
            File.WriteAllText(project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Zava.Sales.Plugins</RootNamespace><VelaPublisherPrefix>zs</VelaPublisherPrefix></PropertyGroup></Project>");

            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--project", project]));

            Assert.Contains("RegisterCustomApi(\"zs_Recalc\", Handle)", File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));
        });
    }

    [Fact]
    public void Add_CustomApi_ExplicitPrefix_WinsOverTheRecordedOne()
    {
        RunInProject("Zava.Plugins", (project, dir) =>
        {
            File.WriteAllText(project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Zava.Plugins</RootNamespace><VelaPublisherPrefix>zs</VelaPublisherPrefix></PropertyGroup></Project>");

            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "ACME", "--project", project]));

            Assert.Contains("RegisterCustomApi(\"acme_Recalc\", Handle)", File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));
        });
    }

    [Fact]
    public void Add_CustomApi_RefusesADerivedPrefixThatIsNotAPublisherPrefix()   // A-50
    {
        // "ZavaSales" derives "zavasales" (9 characters): not a valid publisher prefix, so no unregistrable name is emitted.
        RunInProject("ZavaSales.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["customapi", "Recalc", "--global", "--project", project]));
            Assert.False(File.Exists(Path.Combine(dir, "GlobalCustomApis", "Recalc.cs")));

            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "zava", "--project", project]));
        });
    }

    [Fact]
    public void Add_Step_RefusesOnlyAnExactDuplicateOfAnExistingStep()   // A-50, A-25 rule
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRouter", "--entity", "Account", "--filtering-attributes", "Name", "--project", project]));
            string file = Path.Combine(dir, "Account", "Plugins", "AccountRouter.cs");
            string before = File.ReadAllText(file);

            // Same defaults (Create / PostOperation / Sync) and the same filter column as `add plugin`, under another handler name than its HandleCreate: refused.
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--handler", "OnName", "--filtering-attributes", "Name", "--project", project]));
            Assert.Equal(before, File.ReadAllText(file));

            // Same trigger, other filter column: allowed (several steps per trigger, told apart by their filters).
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--handler", "OnPhone", "--filtering-attributes", "Telephone1", "--project", project]));
            // A different stage is a different trigger.
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--stage", "PreOperation", "--handler", "OnPreCreate", "--filtering-attributes", "Name", "--project", project]));
        });
    }

    private static void RunInProject(string rootNamespace, Action<string, string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string project = Path.Combine(dir, $"{rootNamespace}.csproj");
            File.WriteAllText(project,
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>{rootNamespace}</RootNamespace></PropertyGroup></Project>");
            action(project, dir);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
