using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

// `vela add test` reads the class under test from the source of the projects the test project references, and drives a
// plugin through PluginPipeline the way a caller sends its message.
public class AddTestCommandTests
{
    [Fact]
    public void PluginTest_Update_SetsTheFilteringColumn_AndAssertsTheStepFired()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRenamed", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name,Telephone1", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountRenamed", "--entity", "Account", "--message", "Update", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountRenamedTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.Account.Plugins", content);
            Assert.Contains("using Acme.Plugins.Account.Plugins;", content);
            Assert.Contains("PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountRenamed>();", content);
            Assert.Contains("pipeline.Seed(new Account { Id = id });", content);
            Assert.Contains("pipeline.Service.Update(new Account { Id = id, Name = default, Telephone1 = default });", content);
            Assert.Contains("[Fact]\n        public void Fires_On_Update_PostOperation()", content);
            Assert.Contains("e.PluginTypeName == typeof(AccountRenamed).FullName && e.Message == \"Update\" && e.Stage == Vela.Constants.PluginStages.PostOperation", content);

            // The behaviour test is a skipped skeleton; nothing asserts the scaffold's trace line any more.
            Assert.Contains("[Fact(Skip = \"TODO: assert what AccountRenamed does on Update PostOperation\")]", content);
            Assert.DoesNotContain("TraceCapture", content);
            Assert.DoesNotContain("WithTarget(", content);
        });
    }

    [Fact]
    public void PluginTest_WithoutMessage_DrivesThePluginsOwnStep()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRenamed", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", plugins]));
            // Neither --message nor --entity: both come from the plugin's source.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountRenamed", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountRenamedTests.cs"));
            Assert.Contains("public void Fires_On_Update_PostOperation()", content);
            Assert.Contains("pipeline.Service.Update(new Account { Id = id, Name = default });", content);
        });
    }

    [Fact]
    public void PluginTest_Create_SetsTheFilteringColumn()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountCreated", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountCreated", "--entity", "Account", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountCreatedTests.cs"));
            Assert.Contains("pipeline.Service.Create(new Account { Name = default });", content);
            Assert.Contains("e.Message == \"Create\"", content);
        });
    }

    [Theory]
    [InlineData("Associate")]
    [InlineData("Disassociate")]
    public void PluginTest_AssociationMessages_SendARelationship_NotAnEntityTarget(string message)
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountLinked", "--entity", "Account", "--message", message, "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountLinked", "--entity", "Account", "--message", message, "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountLinkedTests.cs"));
            Assert.DoesNotContain("WithTarget(", content);
            Assert.Contains("pipeline.WithManyToMany(RelationshipName, ", content);
            Assert.Contains($"pipeline.Service.{message}(Account.EntityLogicalNameValue, id, new Relationship(RelationshipName), related);", content);
            Assert.Contains($"e.Message == \"{message}\"", content);
        });
    }

    [Fact]
    public void PluginTest_Delete_DeletesASeededRecordByReference()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--stage", "PreOperation", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountGuard", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountGuardTests.cs"));
            Assert.DoesNotContain("WithTarget(", content);
            Assert.Contains("pipeline.Seed(new Account { Id = id });", content);
            Assert.Contains("pipeline.Service.Delete(Account.EntityLogicalNameValue, id);", content);
        });
    }

    [Fact]
    public void PluginTest_Assign_TargetsAnEntityReference()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountAssigned", "--entity", "Account", "--message", "Assign", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountAssigned", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountAssignedTests.cs"));
            Assert.Contains("new Microsoft.Crm.Sdk.Messages.AssignRequest", content);
            Assert.Contains("Target = new EntityReference(Account.EntityLogicalNameValue, id),", content);
        });
    }

    [Fact]
    public void PluginTest_BulkMessage_SendsTargets()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountBulk", "--entity", "Account", "--message", "CreateMultiple", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountBulk", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountBulkTests.cs"));
            Assert.Contains("new Microsoft.Xrm.Sdk.Messages.CreateMultipleRequest", content);
            Assert.Contains("Targets = new EntityCollection(new Entity[] { new Account { Name = default } }) { EntityName = Account.EntityLogicalNameValue },", content);
            Assert.Contains("e.Message == \"CreateMultiple\"", content);
        });
    }

    [Fact]
    public void PluginTest_RetrieveMultiple_SendsAQuery()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountSearch", "--entity", "Account", "--message", "RetrieveMultiple", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountSearch", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountSearchTests.cs"));
            Assert.Contains("pipeline.Service.RetrieveMultiple(Query.For<Account>().Select(x => x.Id));", content);
            Assert.DoesNotContain("Targets", content);
            Assert.DoesNotContain("ColumnSet(true)", content);
        });
    }

    [Fact]
    public void PluginTest_Retrieve_ReadsThePrimaryKeyOnly()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRead", "--entity", "Account", "--message", "Retrieve", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountRead", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountReadTests.cs"));
            Assert.Contains("    using Vela.Querying;\n", content);
            Assert.Contains("pipeline.Service.Retrieve(Account.EntityLogicalNameValue, id, Query.For<Account>().Select(x => x.Id).Expression.ColumnSet);", content);
            Assert.Contains("//   Account saved = Query.For<Account>().Where(x => x.Id, Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, id).Select(x => x.SomeColumn).FirstOrDefault(pipeline.Service);", content);
            Assert.DoesNotContain("ColumnSet(true)", content);
        });
    }

    [Fact]
    public void PluginTest_RequestTypedMessage_SendsTheStepsRequest_AndIsSkippedUntilItsInputsAreSet()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountState", "--entity", "Account", "--message", "SetState", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountState", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountStateTests.cs"));
            Assert.Contains("pipeline.Service.Execute(new Microsoft.Crm.Sdk.Messages.SetStateRequest", content);
            Assert.Contains("[Fact(Skip = \"TODO: set the SetState request's inputs above, then remove Skip.\")]", content);
            Assert.DoesNotContain("WithTarget(", content);
        });
    }

    [Fact]
    public void PluginTest_UsesTheNamespaceAndEntityOfTheSource()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string sales = Path.Combine(Path.GetDirectoryName(plugins)!, "Sales");
            Directory.CreateDirectory(sales);
            File.WriteAllText(Path.Combine(sales, "OpportunityScorer.cs"),
                "namespace Acme.Plugins.Sales\n{\n    public sealed class OpportunityScorer(string u, string s) : EntityPluginBase<Opportunity>(u, s)\n    {\n" +
                "        protected override void RegisterPluginSteps()\n        {\n            RegisterStep().OnDelete()\n                .PostOperation()\n                .Handle(OnDeleted);\n        }\n    }\n}\n");

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "OpportunityScorer", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Sales", "OpportunityScorerTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.Sales", content);
            Assert.Contains("using Acme.Plugins.Sales;", content);
            Assert.Contains("pipeline.Service.Delete(Opportunity.EntityLogicalNameValue, id);", content);
        });
    }

    [Fact]
    public void PluginTest_ReadsTheStepsOfEveryPartialDeclaration()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(plugins)!, "Account", "Plugins");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "AccountSplit.cs"),
                "namespace Acme.Plugins.Account.Plugins\n{\n    public sealed partial class AccountSplit(string u, string s) : EntityPluginBase<Account>(u, s)\n    {\n    }\n}\n");
            File.WriteAllText(Path.Combine(folder, "AccountSplit.Steps.cs"),
                "namespace Acme.Plugins.Account.Plugins\n{\n    public sealed partial class AccountSplit\n    {\n" +
                "        protected override void RegisterPluginSteps()\n        {\n            RegisterStep().OnUpdate()\n                .PostOperation()\n                .FilterOn(e => e.Telephone1)\n                .Handle(OnChanged);\n        }\n    }\n}\n");

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountSplit", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountSplitTests.cs"));
            Assert.Contains("pipeline.Service.Update(new Account { Id = id, Telephone1 = default });", content);
        });
    }

    [Fact]
    public void PluginTest_MessageAlias_ResolvesToTheStepsRequest()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "EmailSent", "--entity", "Email", "--message", "SendEmail", "--project", plugins]));
            // `Send` is the sdkmessage SendEmail runs as: it must find the OnSendEmail() step, not invent a SendRequest.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "EmailSent", "--message", "Send", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Email", "Plugins", "EmailSentTests.cs"));
            Assert.Contains("public void Fires_On_SendEmail_PostOperation()", content);
            Assert.Contains("new Microsoft.Crm.Sdk.Messages.SendEmailRequest", content);
            Assert.DoesNotContain("SendRequest", content);
        });
    }

    [Fact]
    public void PluginTest_NoReadableStep_AndNoMessage_Fails()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(plugins)!, "Account", "Plugins");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "AccountOpaque.cs"),
                "namespace Acme.Plugins.Account.Plugins\n{\n    public sealed class AccountOpaque(string u, string s) : EntityPluginBase<Account>(u, s)\n    {\n" +
                "        protected override void RegisterPluginSteps() => Steps.RegisterAll(this);\n    }\n}\n");

            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "AccountOpaque", "--project", tests]));
            Assert.Empty(Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories));

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountOpaque", "--message", "Delete", "--project", tests]));
        });
    }

    [Fact]
    public void PluginTest_Disassociate_SeedsTheLink_InsteadOfAssociatingThroughThePipeline()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountUnlinked", "--entity", "Account", "--message", "Disassociate", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountUnlinked", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountUnlinkedTests.cs"));
            Assert.DoesNotContain("pipeline.Service.Associate(", content);
            Assert.Contains("pipeline.Seed(new Entity(\"todo_intersect\", Guid.NewGuid())", content);
        });
    }

    [Fact]
    public void PluginTest_UnknownPlugin_Fails()
    {
        RunInSolution((_, tests, testsDir) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "Missing", "--entity", "Account", "--project", tests]));
            Assert.Empty(Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Test_OutsideTestProject_Fails()
    {
        RunInSolution((plugins, _, _) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "AccountValidator", "--entity", "Account", "--project", plugins]));
        });
    }

    [Fact]
    public void CustomApiTest_Global_UsesTheRegisteredUniqueName()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "zs", "--project", plugins]));
            // The unique name comes from the source, not from --prefix or the project prefix.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "GlobalCustomApis", "RecalcTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.GlobalCustomApis", content);
            Assert.Contains("using Acme.Plugins.GlobalCustomApis;", content);
            Assert.Contains("PluginContext.ForMessage(\"zs_Recalc\")", content);
            Assert.Contains("Assert.Equal(\"zs_Recalc\", definition.UniqueName);", content);
            Assert.Contains("Assert.True(builder.Context.OutputParameters.Contains(output.Name)", content);
            Assert.Contains(".WithInputParameter(", content);
        });
    }

    [Fact]
    public void CustomApiTest_Bound_UsesTheTypedRequest()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "ExportContact", "--entity", "Contact", "--prefix", "acme", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "ExportContact", "--kind", "customapi", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Contact", "CustomApis", "ExportContactTests.cs"));
            Assert.Contains("namespace Acme.Plugins.Tests.Contact.CustomApis", content);
            Assert.Contains("PluginContext.ForRequest(new ExportContactRequest", content);
            Assert.Contains("Assert.Equal(\"acme_ExportContact\", definition.UniqueName);", content);
            Assert.DoesNotContain("ForMessage(", content);

            // Execute validates the request against the definition, so a bound API's test supplies a Target of its table.
            Assert.Contains("if (definition.BoundEntityLogicalName is string boundTable)", content);
            Assert.Contains("[Fact(Skip = \"TODO: set the inputs ExportContact requires\")]", content);
            Assert.Contains("builder.WithInputParameter(\"Target\", new Microsoft.Xrm.Sdk.EntityReference(boundTable, Guid.NewGuid()));", content);
        });
    }

    [Theory]
    [InlineData("AccountEstimateValue")]            // the unique name's suffix, and the class name minus "CustomApi"
    [InlineData("AccountEstimateValueCustomApi")]   // the class name
    public void CustomApiTest_DifferentlyNamedClass_UsesTheDiscoveredNames(string name)
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string apis = Path.Combine(Path.GetDirectoryName(plugins)!, "Account", "CustomApis");
            Directory.CreateDirectory(apis);
            File.WriteAllText(Path.Combine(apis, "AccountEstimateValueCustomApi.cs"), ScaffoldTemplates.AccountEstimateValueCustomApi("Acme", "zava"));

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", name, "--kind", "customapi", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "CustomApis", "AccountEstimateValueCustomApiTests.cs"));
            Assert.Contains("public sealed class AccountEstimateValueCustomApiTests", content);
            Assert.Contains("new AccountEstimateValueCustomApi(string.Empty, string.Empty)", content);
            Assert.Contains("Assert.Equal(\"zava_AccountEstimateValue\", definition.UniqueName);", content);
            Assert.Contains("PluginContext.ForRequest(new EstimateValueRequest", content);
            Assert.DoesNotContain("AccountEstimateValueRequest", content);
        });
    }

    [Fact]
    public void CustomApiTest_MatchesTheUniqueNameSuffix()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string apis = Path.Combine(Path.GetDirectoryName(plugins)!, "GlobalCustomApis");
            Directory.CreateDirectory(apis);
            File.WriteAllText(Path.Combine(apis, "Estimator.cs"), ScaffoldTemplates.AddGlobalCustomApiFile("Acme.Plugins.GlobalCustomApis", "Estimator", "zava_EstimateValue", "AsAction()"));

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "EstimateValue", "--kind", "customapi", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "GlobalCustomApis", "EstimatorTests.cs"));
            Assert.Contains("new Estimator(string.Empty, string.Empty)", content);
            Assert.Contains("PluginContext.ForMessage(\"zava_EstimateValue\")", content);
        });
    }

    [Fact]
    public void CustomApiTest_FindsAClassWithAConventionalConstructor()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string apis = Path.Combine(Path.GetDirectoryName(plugins)!, "Account", "CustomApis");
            Directory.CreateDirectory(apis);
            File.WriteAllText(Path.Combine(apis, "ScoreAccountApi.cs"),
                "namespace Acme.Plugins.Account.CustomApis\n{\n    public sealed class ScoreAccountApi : CustomApiBase<ScoreRequest, ScoreResponse>\n    {\n" +
                "        public ScoreAccountApi(string u, string s) : base(u, s) { }\n\n" +
                "        protected override void RegisterCustomApi() => RegisterCustomApi(\"acme_ScoreAccount\").AsFunction();\n    }\n}\n");

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "ScoreAccount", "--kind", "customapi", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "CustomApis", "ScoreAccountApiTests.cs"));
            Assert.Contains("new ScoreAccountApi(string.Empty, string.Empty)", content);
            Assert.Contains("PluginContext.ForRequest(new ScoreRequest", content);
        });
    }

    [Fact]
    public void CustomApiTest_UnknownCustomApi_Fails()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "acme", "--project", plugins]));

            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "Missing", "--kind", "customapi", "--project", tests]));
            Assert.Empty(Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void PluginTest_WithoutMessage_TestsEveryStep()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Telephone1", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountTouchedTests.cs"));
            Assert.Contains("public void Fires_On_Create_PostOperation()", content);
            Assert.Contains("public void Does_What_It_Should_On_Create_PostOperation()", content);
            Assert.Contains("public void Fires_On_Update_PostOperation()", content);
            Assert.Contains("public void Does_What_It_Should_On_Update_PostOperation()", content);
            Assert.Contains("pipeline.Service.Update(new Account { Id = id, Telephone1 = default });", content);
        });
    }

    [Fact]
    public void PluginTest_ExistingClass_AddsOnlyTheNewStepsTests_AndKeepsEveryOtherByte()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));

            // The user edits the generated class: a test of their own, a comment, a helper type after it.
            string path = Path.Combine(testsDir, "Account", "Plugins", "AccountTouchedTests.cs");
            string generated = File.ReadAllText(path);
            int classClose = generated.LastIndexOf("    }\n}", StringComparison.Ordinal);
            string edited = generated.Substring(0, classClose) +
                "\n        // Mine: keep it.\n        [Fact]\n        public void My_Own_Test() { Assert.True(true); }\n" +
                generated.Substring(classClose) + "\n// trailing user comment\n";
            File.WriteAllText(path, edited);

            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));

            string completed = File.ReadAllText(path);
            int insertAt = edited.LastIndexOf("    }\n}", StringComparison.Ordinal);
            Assert.StartsWith(edited.Substring(0, insertAt), completed, StringComparison.Ordinal);
            Assert.EndsWith(edited.Substring(insertAt), completed, StringComparison.Ordinal);
            string added = completed.Substring(insertAt, completed.Length - edited.Length);
            Assert.StartsWith("\n        [Fact]\n        public void Fires_On_Delete_PostOperation()", added, StringComparison.Ordinal);
            Assert.Contains("pipeline.Service.Delete(Account.EntityLogicalNameValue, id);", added);
            Assert.Contains("public void Does_What_It_Should_On_Delete_PostOperation()", added);
            Assert.DoesNotContain("Create", added);
            Assert.DoesNotContain("using ", added);

            // Every step has its test now: a rerun changes nothing and succeeds.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            Assert.Equal(completed, File.ReadAllText(path));
        });
    }

    [Fact]
    public void PluginTest_ExistingClass_ForTheMessageGiven_AddsThatStepOnly()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));
            string path = Path.Combine(testsDir, "Account", "Plugins", "AccountTouchedTests.cs");
            Assert.DoesNotContain("Fires_On_Delete", File.ReadAllText(path));

            // Create is tested already: nothing to add.
            string before = File.ReadAllText(path);
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));
            Assert.Equal(before, File.ReadAllText(path));

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Delete", "--project", tests]));
            Assert.Contains("public void Fires_On_Delete_PostOperation()", File.ReadAllText(path));
        });
    }

    // Regression: with several steps on the --message, the command used to take the first one only, so a second
    // Create step added later never got its tests (the first was tested already: "nothing to add").
    [Theory]
    [InlineData("PreOperation", "Create_PreOperation")]
    [InlineData("PostOperation", "Create_PostOperation_2")]
    public void PluginTest_ExistingClass_ForTheMessageGiven_AddsTheTestsOfEveryUntestedStepOnThatMessage(string stage, string newName)
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));
            string path = Path.Combine(testsDir, "Account", "Plugins", "AccountTouchedTests.cs");
            string before = File.ReadAllText(path);

            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Create", "--handler", "HandleCreateAgain", "--stage", stage, "--filtering-attributes", "Telephone1", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));

            string completed = File.ReadAllText(path);
            string added = completed.Substring(before.LastIndexOf("    }\n}", StringComparison.Ordinal), completed.Length - before.Length);
            Assert.Contains($"public void Fires_On_{newName}()", added);
            Assert.Contains($"public void Does_What_It_Should_On_{newName}()", added);
            Assert.Contains("Telephone1 = default", added);
            Assert.Single(Regex.Matches(completed, @"public void Fires_On_Create_PostOperation\(\)"));

            // Both Create steps are tested now: a rerun changes nothing.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));
            Assert.Equal(completed, File.ReadAllText(path));
        });
    }

    [Fact]
    public void PluginTest_NewClass_ForTheMessageGiven_TestsEveryStepOnThatMessage()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Create", "--handler", "HandleCreateAgain", "--stage", "PreOperation", "--filtering-attributes", "Telephone1", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--message", "Create", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountTouchedTests.cs"));
            Assert.Contains("public void Fires_On_Create_PostOperation()", content);
            Assert.Contains("public void Fires_On_Create_PreOperation()", content);
            Assert.DoesNotContain("Fires_On_Delete", content);
        });
    }

    [Fact]
    public void PluginTest_ExistingClass_RecognisesTheMessageOnlyNameOfEarlierVersions()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete", "--project", plugins]));

            // A class written by an earlier `vela add test` (Fires_On_<Message>), in a file-scoped namespace, with
            // CRLF line endings and without the usings the new tests need.
            string folder = Path.Combine(testsDir, "Account", "Plugins");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "AccountTouchedTests.cs");
            string legacy = "using Xunit;\r\n\r\nnamespace Acme.Plugins.Tests.Account.Plugins;\r\n\r\npublic sealed class AccountTouchedTests\r\n{\r\n" +
                "    [Fact]\r\n    public void Fires_On_Create() { }\r\n}\r\n";
            File.WriteAllText(path, legacy);

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));

            string completed = File.ReadAllText(path);
            Assert.DoesNotContain("Fires_On_Create_PostOperation", completed);
            Assert.Contains("\r\n    [Fact]\r\n    public void Fires_On_Delete_PostOperation()\r\n    {\r\n", completed);
            Assert.DoesNotContain("\r\r", completed);
            Assert.Equal(completed.Split('\n').Length - 1, completed.Split("\r\n").Length - 1);
            Assert.StartsWith(
                "using Xunit;\r\nusing System;\r\nusing Microsoft.Xrm.Sdk;\r\nusing Vela.Querying;\r\nusing Vela.Testing;\r\nusing Acme.Plugins.Account.Plugins;\r\nusing Acme.Shared.Models.Generated;\r\n\r\nnamespace",
                completed,
                StringComparison.Ordinal);
            Assert.EndsWith("    }\r\n}\r\n", completed, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("namespace Acme.Plugins.Tests.Account.Plugins\n{\n    public sealed class SomethingElse\n    {\n    }\n}\n")]
    [InlineData("namespace Acme.Plugins.Tests.Account.Plugins\n{\n    public sealed class AccountTouchedTests\n    {\n        public void Broken()\n        {\n    }\n")]
    public void PluginTest_ExistingFileWithoutAReadableClass_Fails_AndLeavesItAlone(string text)
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
            string folder = Path.Combine(testsDir, "Account", "Plugins");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "AccountTouchedTests.cs");
            File.WriteAllText(path, text);

            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            Assert.Equal(text, File.ReadAllText(path));

            // --force still overwrites it with a fresh class.
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--force", "--project", tests]));
            Assert.Contains("public void Fires_On_Create_PostOperation()", File.ReadAllText(path));
        });
    }

    [Fact]
    public void CustomApiTest_ExistingClass_HasNothingToAdd()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["customapi", "Recalc", "--global", "--prefix", "zs", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--project", tests]));
            string path = Path.Combine(testsDir, "GlobalCustomApis", "RecalcTests.cs");
            string before = File.ReadAllText(path) + "// mine\n";
            File.WriteAllText(path, before);

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--project", tests]));
            Assert.Equal(before, File.ReadAllText(path));

            File.WriteAllText(path, "// not a test class\n");
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "Recalc", "--kind", "customapi", "--project", tests]));
        });
    }

    [Fact]
    public void PluginTest_ExistingClass_KeepsABehaviourTestWhoseDispatchTestWasRemoved()
    {
        CompleteTouched(
            "namespace Acme.Plugins.Tests.Account.Plugins\n{\n    using System;\n    using Microsoft.Xrm.Sdk;\n    using Vela.Querying;\n    using Vela.Testing;\n    using Xunit;\n" +
            "    using Acme.Plugins.Account.Plugins;\n    using Acme.Shared.Models.Generated;\n\n    public sealed class AccountTouchedTests\n    {\n" +
            "        [Fact]\n        public void Fires_On_Create_PostOperation() { }\n\n" +
            "        [Fact]\n        public void Does_What_It_Should_On_Delete_PostOperation() { Assert.True(true); }\n    }\n}\n",
            completed =>
            {
                Assert.Contains("public void Fires_On_Delete_PostOperation()", completed);
                Assert.Single(Regex.Matches(completed, @"void Does_What_It_Should_On_Delete_PostOperation\("));
            });
    }

    [Fact]
    public void PluginTest_ExistingClass_KeepsTheFilesEncodingAndByteOrderMark()
    {
        string text = "namespace Acme.Plugins.Tests.Account.Plugins\n{\n    using System;\n    using Microsoft.Xrm.Sdk;\n    using Vela.Querying;\n    using Vela.Testing;\n    using Xunit;\n" +
            "    using Acme.Plugins.Account.Plugins;\n    using Acme.Shared.Models.Generated;\n\n    // café\n    public sealed class AccountTouchedTests\n    {\n" +
            "        [Fact]\n        public void Fires_On_Create_PostOperation() { }\n    }\n}\n";
        foreach (System.Text.Encoding encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
        {
            RunInSolution((plugins, tests, testsDir) =>
            {
                string path = TouchedTestPath(plugins, testsDir);
                File.WriteAllText(path, text, encoding);
                byte[] before = File.ReadAllBytes(path);
                int head = encoding.GetPreamble().Length + encoding.GetByteCount(text.Substring(0, text.LastIndexOf("    }\n}", StringComparison.Ordinal)));

                Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));

                byte[] after = File.ReadAllBytes(path);
                Assert.Equal(before.Take(head), after.Take(head));
                Assert.Contains("Fires_On_Delete_PostOperation", encoding.GetString(after, encoding.GetPreamble().Length, after.Length - encoding.GetPreamble().Length));
            });
        }
    }

    [Fact]
    public void PluginTest_ExistingClass_CompactFile_AddsTheUsingsBeforeTheNamespace()
    {
        CompleteTouched(
            "using Xunit; namespace Acme.Plugins.Tests.Account.Plugins { public sealed class AccountTouchedTests { [Fact] public void Fires_On_Create_PostOperation() { } } }\n",
            completed =>
            {
                Assert.StartsWith("using Xunit;\nusing System;\nusing Microsoft.Xrm.Sdk;\nusing Vela.Querying;\nusing Vela.Testing;\nusing Acme.Plugins.Account.Plugins;\nusing Acme.Shared.Models.Generated; namespace", completed, StringComparison.Ordinal);
                Assert.Contains("public void Fires_On_Delete_PostOperation()", completed);
            });
    }

    [Fact]
    public void PluginTest_ExistingClass_CountsOnlyTheUsingsTheClassSees()
    {
        CompleteTouched(
            "namespace Other\n{\n    using Xunit;\n}\n\nnamespace Acme.Plugins.Tests.Account.Plugins\n{\n    public sealed class AccountTouchedTests\n    {\n" +
            "        [Xunit.Fact]\n        public void Fires_On_Create_PostOperation() { }\n    }\n}\n",
            completed =>
            {
                Assert.StartsWith("using System;\nusing Microsoft.Xrm.Sdk;\nusing Vela.Querying;\nusing Vela.Testing;\nusing Xunit;\n", completed, StringComparison.Ordinal);
                Assert.Contains("namespace Other\n{\n    using Xunit;\n}\n", completed);
            });
    }

    [Fact]
    public void PluginTest_ExistingClass_ACallOrANestedMethodIsNotTheStepsTest()
    {
        CompleteTouched(
            "namespace Acme.Plugins.Tests.Account.Plugins\n{\n    using System;\n    using Microsoft.Xrm.Sdk;\n    using Vela.Querying;\n    using Vela.Testing;\n    using Xunit;\n" +
            "    using Acme.Plugins.Account.Plugins;\n    using Acme.Shared.Models.Generated;\n\n    public sealed class AccountTouchedTests\n    {\n" +
            "        [Fact]\n        public void Fires_On_Create_PostOperation() { new Helper().Fires_On_Delete_PostOperation(); }\n\n" +
            "        private void Run() => Fires_On_Delete_PostOperation();\n\n" +
            "        private sealed class Helper\n        {\n            public void Fires_On_Delete_PostOperation() { }\n        }\n    }\n}\n",
            completed => Assert.Contains("        [Fact]\n        public void Fires_On_Delete_PostOperation()\n", completed));
    }

    [Fact]
    public void PluginTest_ExistingClassWithoutABody_FollowedByAnotherClass_Fails()
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string path = TouchedTestPath(plugins, testsDir);
            string text = "namespace Acme.Plugins.Tests.Account.Plugins\n{\n    public sealed partial class AccountTouchedTests;\n\n    public sealed class Helper\n    {\n    }\n}\n";
            File.WriteAllText(path, text);

            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            Assert.Equal(text, File.ReadAllText(path));
        });
    }

    // AccountTouched registers Create then Delete; its test file holds <paramref name="text"/> before `vela add test`.
    private static void CompleteTouched(string text, Action<string> assert)
    {
        RunInSolution((plugins, tests, testsDir) =>
        {
            string path = TouchedTestPath(plugins, testsDir);
            File.WriteAllText(path, text);

            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountTouched", "--project", tests]));
            assert(File.ReadAllText(path));
        });
    }

    private static string TouchedTestPath(string plugins, string testsDir)
    {
        Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountTouched", "--entity", "Account", "--filtering-attributes", "Name", "--project", plugins]));
        Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountTouched", "--entity", "Account", "--message", "Delete", "--handler", "HandleDelete", "--project", plugins]));
        string folder = Path.Combine(testsDir, "Account", "Plugins");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "AccountTouchedTests.cs");
    }

    // A solution with a plugins project (Acme.Plugins) and a test project referencing it (Acme.Plugins.Tests), as
    // `vela init` lays them out: action(pluginsProject, testsProject, testsDir).
    private static void RunInSolution(Action<string, string, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-addtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            string testsDir = Path.Combine(root, "tests", "Acme.Plugins.Tests");
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(root, "Acme.slnx"), "<Solution />");
            string plugins = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(plugins, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            string tests = Path.Combine(testsDir, "Acme.Plugins.Tests.csproj");
            File.WriteAllText(tests,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins.Tests</RootNamespace></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"..\\..\\src\\Acme.Plugins\\Acme.Plugins.csproj\" /></ItemGroup></Project>");
            action(plugins, tests, testsDir);
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
}
