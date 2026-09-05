using System;
using System.IO;
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
        string created = Assert.Single(System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Select(doc.RootElement.GetProperty("created").EnumerateArray(), e => e.GetString())));
        Assert.Equal("/proj/Account/Plugins/Foo.cs", created);
    }

    [Fact]
    public void Add_Plugin_GeneratesTypedPlugin_WithInferredNamespaces()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountValidatorPlugin", "--entity", "Account", "--message", "Update", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Account", "Plugins", "AccountValidatorPlugin.cs");
            Assert.True(File.Exists(file));

            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Account.Plugins", content);
            Assert.Contains("using Acme.Shared.Models.Generated;", content);
            Assert.Contains("public sealed class AccountValidatorPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)", content);
            Assert.Contains("RegisterSteps().OnUpdate()", content);
        });
    }

    [Theory]
    [InlineData("Associate", "RegisterSteps().OnAssociate()")]
    [InlineData("retrievemultiple", "RegisterSteps().OnRetrieveMultiple()")]
    [InlineData("CreateMultiple", "RegisterSteps().OnCreateMultiple()")]
    [InlineData("UpdateMultiple", "RegisterSteps().OnUpdateMultiple()")]
    public void Add_Plugin_KnownMessage_RendersTypedCall(string message, string expectedCall)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains(expectedCall, content);
        });
    }

    [Fact]
    public void Add_Plugin_CustomMessage_RendersOnMessageCall()
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountCustom", "--entity", "Account", "--message", "acme_DoThing", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountCustom.cs"));
            Assert.Contains("RegisterSteps().OnMessage(\"acme_DoThing\")", content);
        });
    }

    // CreateMultiple/UpdateMultiple map to the typed OnCreateMultiple()/OnUpdateMultiple() builder methods,
    // which require a filtering attribute at compile time - so the generator must also emit the filtering call,
    // otherwise the scaffolded plugin would not compile.
    [Theory]
    [InlineData("CreateMultiple", "RegisterSteps().OnCreateMultiple()")]
    [InlineData("UpdateMultiple", "RegisterSteps().OnUpdateMultiple()")]
    public void Add_Plugin_BulkMessage_RendersTypedCallWithFiltering(string message, string expectedCall)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountBulk", "--entity", "Account", "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountBulk.cs"));
            Assert.Contains(expectedCall, content);
            Assert.Contains(".FilteringAttributes(", content);
        });
    }

    // SetState/SetStateDynamicEntity are deprecated by the platform (use an Update of statecode/statuscode),
    // so the builder no longer exposes a typed On...() method for them. `vela add` treats them as any other
    // custom message and renders the permissive OnMessage("...") escape hatch.
    [Theory]
    [InlineData("SetState")]
    [InlineData("SetStateDynamicEntity")]
    public void Add_Plugin_SetState_RendersOnMessageCall(string message)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains($"RegisterSteps().OnMessage(\"{message}\")", content);
        });
    }

    // Every message with a typed builder method (including the Tier-2 Merge / sharing messages) renders the
    // strongly-typed On...() call rather than the permissive OnMessage("...") escape hatch.
    [Theory]
    [InlineData("Merge")]
    [InlineData("GrantAccess")]
    [InlineData("ModifyAccess")]
    [InlineData("RevokeAccess")]
    [InlineData("Delete")]
    public void Add_Plugin_KnownMessage_RendersTypedOnCall(string message)
    {
        RunInProject("Acme.Plugins", (project, dir) =>
        {
            int exit = AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, "--project", project, "--force"]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains($"RegisterSteps().On{message}()", content);
            Assert.DoesNotContain("OnMessage(", content);
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
            Assert.Contains("RegisterCustomApi(\"acme_RecalculateTotals\", nameof(Handle))", content);
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
    public void Add_Service_WithoutEntity_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["service", "OrphanService", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Add_PluginTest_GeneratesTestTargetingThePlugin()
    {
        RunInProject("Acme.Plugins.Tests", (project, dir) =>
        {
            int exit = AddCommand.Run(["test", "AccountValidatorPlugin", "--entity", "Account", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string file = Path.Combine(dir, "Account", "Plugins", "AccountValidatorPluginTests.cs");
            Assert.True(File.Exists(file));

            string content = File.ReadAllText(file);
            Assert.Contains("namespace Acme.Plugins.Tests.Account.Plugins", content);
            Assert.Contains("using Acme.Plugins.Account.Plugins;", content);
            Assert.Contains("using Acme.Shared.Models.Generated;", content);
            Assert.Contains("public sealed class AccountValidatorPluginTests", content);
            Assert.Contains("PluginContext.For<Account>()", content);
            Assert.Contains("new AccountValidatorPlugin(string.Empty, string.Empty)", content);
        });
    }

    [Fact]
    public void Add_GlobalCustomApiTest_GeneratesTestWithDerivedUniqueName()
    {
        RunInProject("Acme.Plugins.Tests", (project, dir) =>
        {
            int exit = AddCommand.Run(["test", "RecalculateTotals", "--kind", "customapi", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "GlobalCustomApis", "RecalculateTotalsTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.GlobalCustomApis", content);
            Assert.Contains("using Acme.Plugins.GlobalCustomApis;", content);
            Assert.Contains("PluginContext.ForMessage(\"acme_RecalculateTotals\")", content);
            Assert.Contains("new RecalculateTotals(string.Empty, string.Empty)", content);
        });
    }

    [Fact]
    public void Add_BoundCustomApiTest_GeneratesTestUnderEntityFolder()
    {
        RunInProject("Acme.Plugins.Tests", (project, dir) =>
        {
            int exit = AddCommand.Run(["test", "ExportContact", "--kind", "customapi", "--entity", "Contact", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(Path.Combine(dir, "Contact", "CustomApis", "ExportContactTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.Contact.CustomApis", content);
            Assert.Contains("using Acme.Plugins.Contact.CustomApis;", content);
            Assert.Contains("PluginContext.ForMessage(\"acme_ExportContact\")", content);
        });
    }

    [Fact]
    public void Add_Test_OutsideTestProject_Fails()
    {
        RunInProject("Acme.Plugins", (project, _) =>
        {
            int exit = AddCommand.Run(["test", "AccountValidatorPlugin", "--entity", "Account", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
        });
    }

    [Fact]
    public void Add_PluginTest_WithoutEntity_Fails()
    {
        RunInProject("Acme.Plugins.Tests", (project, _) =>
        {
            int exit = AddCommand.Run(["test", "AccountValidatorPlugin", "--project", project]);
            Assert.Equal(ExitInvalidArguments, exit);
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
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--project", project]));
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "DupPlugin", "--entity", "Account", "--project", project, "--force"]));
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

            int exit = AddCommand.Run(["step", "AccountRouter", "--entity", "Account", "--message", "Update", "--project", project]);
            Assert.Equal(ExitSuccess, exit);

            string content = File.ReadAllText(pluginPath);
            Assert.Contains("RegisterSteps().OnUpdate()", content);
            Assert.Contains(".Handler(HandleUpdate);", content);
            Assert.Contains("private void HandleUpdate(ILocalPluginContext ctx)", content);
            // The original step is preserved.
            Assert.Contains(".Handler(HandleCreate);", content);
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
    using Vela.StepBuilder;
    using Acme.Shared.Models.Generated;

    public sealed class AccountRouter : EntityPluginBase<Account>
    {
        public AccountRouter(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .Handler(HandleCreate);
        }

        private void HandleCreate(ILocalPluginContext ctx)
        {
        }
    }
}
""";

    // Creates a temp directory containing a minimal .csproj with the given RootNamespace, then invokes the action
    // with the project file path and its directory.
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
