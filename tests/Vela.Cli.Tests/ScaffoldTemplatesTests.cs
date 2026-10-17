using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class ScaffoldTemplatesTests
{
    private static ScaffoldOptions Options() => new(
        SolutionName: "ZavaSales",
        Prefix: "Zava",
        OutputDirectory: ".",
        IncludeConsole: true,
        IncludeIntegrations: true,
        IncludeTests: true,
        CoreVersion: "1.0.0",
        Force: false,
        Interactive: false,
        IncludeSamples: true);

    [Fact]
    public void ApiPrefix_IsLowerCasedAndDotless()
    {
        Assert.Equal("zavacrm", ScaffoldTemplates.ApiPrefix("Zava.CRM"));
    }

    [Fact]
    public void TestsCsproj_KeepsTheSolutionConfigurationOnItsReferences()
    {
        string csproj = ScaffoldTemplates.TestsCsproj("Zava.Plugins.Tests", "Zava.Plugins", "Zava.Shared", "1.0.0");

        // An unmatched reference would otherwise build the plugin project a second time (Debug) in parallel, and
        // both builds pack into the same artifacts/<Plugin>.1.0.0.nupkg (#406).
        Assert.Contains("<ShouldUnsetParentConfigurationAndPlatform>false</ShouldUnsetParentConfigurationAndPlatform>", csproj);
    }

    [Fact]
    public void TestsCsproj_ReferencesSystemNetHttp()
    {
        string csproj = ScaffoldTemplates.TestsCsproj("Zava.Integrations.Tests", "Zava.Integrations", "Zava.Shared", "1.0.0");

        // On net48 a test that builds new HttpClient(fakeHandler) needs the System.Net.Http GAC facade, as the plugin
        // project does; every scaffolded test project (plugins and integrations) comes from this template.
        Assert.Contains("<Reference Include=\"System.Net.Http\" />", csproj);
    }

    [Fact]
    public void PluginCsproj_ReferencesPluginSdkCoreAndShared()
    {
        string csproj = ScaffoldTemplates.PluginCsproj("Zava.Plugins", "Zava.Shared", "1.0.0");

        Assert.Contains("Microsoft.PowerApps.MSBuild.Plugin", csproj);
        Assert.Contains("Vela.Plugins", csproj);
        Assert.Contains("Zava.Shared\\Zava.Shared.csproj", csproj);
        Assert.Contains("<GeneratePackageOnBuild>true</GeneratePackageOnBuild>", csproj);
        // The build (not `dotnet pack`) produces the plugin package; PackageOutputPath drops it in artifacts/.
        Assert.Contains("<PackageOutputPath>$(MSBuildProjectDirectory)\\..\\..\\artifacts</PackageOutputPath>", csproj);
        Assert.Contains("<TargetFramework>net48</TargetFramework>", csproj);
        // Early-bound entities passed to ctx.UserService inside a plugin need the assembly marked as a proxy-types
        // assembly, or the sandbox throws a SerializationException at runtime.
        Assert.Contains("<AssemblyAttribute Include=\"Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute\" />", csproj);
        // HttpClient on .NET Framework binds against the System.Net.Http GAC facade; the scaffold references it so an
        // outbound (e.g. managed-identity) call compiles and loads out of the box.
        Assert.Contains("<Reference Include=\"System.Net.Http\" />", csproj);
    }

    [Fact]
    public void PluginCsproj_KeepsWildcardAssemblyVersion_ButSilencesCS7035()
    {
        // The 1.0.* assembly version is deliberate (Dataverse caches plugin assemblies by version, so the wildcard
        // auto-increments to bust the cache). That wildcard makes the compiler emit CS7035; the project suppresses it
        // so a freshly scaffolded/`vela add`-ed plugin builds warning-free without losing the cache-busting behaviour.
        string csproj = ScaffoldTemplates.PluginCsproj("Zava.Plugins", "Zava.Shared", "1.0.0");

        Assert.Contains("<AssemblyVersion>1.0.*</AssemblyVersion>", csproj);
        Assert.Contains("CS7035", csproj);
    }

    // Every demo plugin: the class must be `partial` when it captures a typed image (a WithPreImage/WithPostImage
    // column-selector lambda) OR uses a generated typed step context, because the source generator (VELA014-016)
    // emits a second `partial` half of the class. A non-partial declaration fails the build with CS0260 - the very
    // thing that broke `vela init`.
    public static TheoryData<string> DemoPluginTemplates() => new()
    {
        ScaffoldTemplates.AccountRelatedContactsPlugin("Zava"),
        ScaffoldTemplates.AccountPostCreatePlugin("Zava"),
        ScaffoldTemplates.AccountMessageReference("Zava"),
        ScaffoldTemplates.ContactRouterPlugin("Zava"),
        ScaffoldTemplates.AccountSyncRouterPlugin("Zava"),
        ScaffoldTemplates.ContactSyncPlugin("Zava"),
    };

    [Theory]
    [MemberData(nameof(DemoPluginTemplates))]
    public void DemoPlugin_ThatCapturesTypedImage_IsPartial(string code)
    {
        bool capturesTypedImage =
            code.Contains(".WithPreImage(") && code.Contains("=>") ||
            code.Contains(".WithPostImage(") && code.Contains("=>");
        if (!capturesTypedImage)
        {
            return;
        }

        Assert.Contains("public sealed partial class", code);
        Assert.DoesNotContain("public sealed class", code);
    }

    [Fact]
    public void SharedCsproj_ReferencesDataAnnotations_SoGeneratedModelsCompileOnNet48()
    {
        string csproj = ScaffoldTemplates.SharedCsproj(Options());

        // `vela models` emits [StringLength]/[Range] DataAnnotations by default; net48 must reference the
        // System.ComponentModel.DataAnnotations framework assembly or the generated models fail to compile.
        Assert.Contains("System.ComponentModel.DataAnnotations", csproj);
    }

    [Fact]
    public void DirectoryBuildProps_DisablesCentralPackageManagement()
    {
        string props = ScaffoldTemplates.DirectoryBuildProps();

        // Insulates the scaffolded solution from a parent Directory.Packages.props if created inside another repo.
        Assert.Contains("<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>", props);
    }

    // The generated On<Message>() step extensions need Vela.Plugins: the Shared project (Vela.Messages only) leaves
    // them out and every plugin project compiles them.
    [Fact]
    public void StepExtensions_AreCompiledByThePluginProject_NotByShared()
    {
        Assert.Contains("<Compile Remove=\"Models\\Generated\\Messages\\*.Steps.cs\" />", ScaffoldTemplates.SharedCsproj(Options()));
        Assert.Contains("<Compile Include=\"..\\Acme.Shared\\Models\\Generated\\Messages\\*.Steps.cs\"", ScaffoldTemplates.PluginCsproj("Acme.Plugins", "Acme.Shared", "1.0.0"));
    }

    [Fact]
    public void PluginCsproj_OmitsSigningTarget_ByDefault_AndIncludesItWhenRequested()
    {
        string plain = ScaffoldTemplates.PluginCsproj("Zava.Plugins", "Zava.Shared", "1.0.0");
        Assert.DoesNotContain("SignPluginPackage", plain);

        string signed = ScaffoldTemplates.PluginCsproj("Zava.Integrations", "Zava.Shared", "1.0.0", withSigningTarget: true);
        Assert.Contains("<Target Name=\"SignPluginPackage\"", signed);
        Assert.Contains("vela sign --package", signed);
        // Opt-in (needs a fingerprint) and CI-gated.
        Assert.Contains("'$(PluginSigningFingerprint)' != ''", signed);
        Assert.Contains("'$(GITHUB_ACTIONS)' != 'true'", signed);
        Assert.Contains("'$(TF_BUILD)' != 'True'", signed);
        // Both certificate sources are wired: fingerprint (store) and a .pfx file (credential or password).
        // A-65: every MSBuild property is quoted, and the password reaches vela through an environment variable,
        // never its command line.
        Assert.Contains("--fingerprint &quot;$(PluginSigningFingerprint)&quot;", signed);
        Assert.Contains("--pfx-credential &quot;$(PluginSigningCredential)&quot;", signed);
        Assert.Contains("EnvironmentVariables=\"VELA_SIGN_PFX_PASSWORD=$([MSBuild]::Escape('$(PluginSigningPassword)'))\"", signed);
        Assert.Contains("--pfx-password-env VELA_SIGN_PFX_PASSWORD", signed);
        Assert.DoesNotContain("--pfx-password $(", signed);

        // Both must be well-formed XML — guards against, e.g., a '--' sequence inside an XML comment.
        System.Xml.Linq.XDocument.Parse(plain);
        System.Xml.Linq.XDocument.Parse(signed);
    }

    [Fact]
    public void SingleStepPlugin_HasRequiredCtorAndDiUsage()
    {
        string code = ScaffoldTemplates.AccountPostCreatePlugin("Zava");

        Assert.Contains("namespace Zava.Plugins.Account.Plugins", code);
        Assert.Contains("EntityPluginBase<Account>", code);
        Assert.Contains("(string unsecureConfig, string secureConfig)", code);
        Assert.Contains("ConfigureServices", code);
        Assert.Contains("GetRequiredService<IAccountFollowUpService>", code);
        // Binds a typed post-stage handler that resolves the DI service and reads the typed response.
        Assert.Contains(".Handle(OnAccountCreated)", code);
        Assert.Contains("ICreatePostContext<Account>", code);
        Assert.Contains("ctx.Response.id", code);
    }

    [Fact]
    public void MessageReference_ShowcasesEveryTypedMessage_AsDisabledSteps()
    {
        string code = ScaffoldTemplates.AccountMessageReference("Zava");

        // Abstract: it compiles but neither vela nor Dataverse registers it, so its steps are never deployed.
        Assert.Contains("public abstract partial class AccountMessageReference(", code);

        // One typed handler per modelled message shown on account (CRUD/bulk, relationships, merge, sharing,
        // and the account-valid sharing reads).
        string[] contexts =
        {
            "ICreatePostContext<Account>", "IUpdatePostContext<Account>", "IDeletePreContext<Account>",
            "ICreateMultiplePostContext<Account>", "IUpdateMultiplePostContext<Account>",
            "IRetrievePostContext<Account>", "IRetrieveMultiplePostContext<Account>",
            "IAssociatePostContext<Account>", "IDisassociatePostContext<Account>", "IMergePostContext<Account>",
            "IGrantAccessPostContext<Account>", "IModifyAccessPostContext<Account>", "IRevokeAccessPostContext<Account>",
            "IAssignPostContext<Account>",
            "IRetrievePrincipalAccessPostContext<Account>", "IRetrieveSharedPrincipalsAndAccessPostContext<Account>",
        };
        foreach (string ctxType in contexts)
        {
            Assert.Contains(ctxType, code);
        }

        // Every message reads its primary record via ctx.Request.Target (early-bound on Create / Update) /
        // ctx.Request.Targets (the SDK-typed EntityReference / EntityCollection for the rest); there is no
        // ctx.Target context property, no ctx.TargetReference / ctx.TargetCollection accessors, and never a raw cast.
        Assert.Contains("ctx.Request.Target", code);
        Assert.Contains("ctx.Request.Targets", code);
        Assert.DoesNotContain("ctx.TargetReference", code);
        Assert.DoesNotContain("ctx.TargetCollection", code);
        Assert.DoesNotContain(".ToEntity<", code);

        // Every step is dormant and binds a handler: each registration ends with .Disabled().Handle(...) (16).
        Assert.Equal(16, CountOccurrences(code, ".Disabled().Handle("));
        Assert.Equal(16, CountOccurrences(code, ".Handle("));
    }

    [Fact]
    public void AccountFollowUpService_IsTableScoped_AndSchedulesSevenDaysOut()
    {
        string code = ScaffoldTemplates.AccountFollowUpService("Zava");

        Assert.Contains("namespace Zava.Plugins.Account.Services", code);
        Assert.Contains("interface IAccountFollowUpService", code);
        Assert.Contains("new(\"task\")", code); // target-typed new
        Assert.Contains("AddDays(FollowUpDays)", code);
        Assert.Contains("FollowUpDays = 7", code);
    }

    [Fact]
    public void AddServiceFile_EmitsTableScopedInterfaceAndImplementation()
    {
        string code = ScaffoldTemplates.AddServiceFile(
            "Acme.Plugins.Account.Services", "IAccountFollowUpService", "AccountFollowUpService", "Account", "Acme.Shared.Models.Generated");

        Assert.Contains("namespace Acme.Plugins.Account.Services", code);
        Assert.Contains("using Acme.Shared.Models.Generated;", code);
        Assert.Contains("public interface IAccountFollowUpService", code);
        Assert.Contains("public sealed class AccountFollowUpService(IOrganizationService service) : IAccountFollowUpService", code);
        // Table-scoped services take the current-user IOrganizationService via a primary constructor.
        Assert.Contains("private readonly IOrganizationService _service = service;", code);
        Assert.Contains("void Execute(Account target)", code);
    }

    [Fact]
    public void AddPluginTestFile_DrivesThePluginThroughThePipeline()
    {
        string code = ScaffoldTemplates.AddPluginTestFile(
            "Acme.Plugins.Tests.Account.Plugins", "Acme.Plugins.Account.Plugins", "Acme.Shared.Models.Generated",
            "AccountValidatorPluginTests", "AccountValidatorPlugin", "Account",
            [(new ScaffoldTemplates.PluginTestStep("Update", "Microsoft.Xrm.Sdk.Messages.UpdateRequest", ["Name"], "PostOperation"), "Update_PostOperation")]);

        Assert.Contains("namespace Acme.Plugins.Tests.Account.Plugins", code);
        Assert.Contains("using Acme.Plugins.Account.Plugins;", code);
        Assert.Contains("public sealed class AccountValidatorPluginTests", code);
        Assert.Contains("PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountValidatorPlugin>();", code);
        Assert.Contains("pipeline.Service.Update(new Account { Id = id, Name = default });", code);
        Assert.Contains("Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName == typeof(AccountValidatorPlugin).FullName && e.Message == \"Update\" && e.Stage == Vela.Constants.PluginStages.PostOperation);", code);
        Assert.Contains("public void Fires_On_Update_PostOperation()", code);
        Assert.Contains("[Fact(Skip = \"TODO: assert what AccountValidatorPlugin does on Update PostOperation\")]", code);
        Assert.Contains("public void Does_What_It_Should_On_Update_PostOperation()", code);
        Assert.DoesNotContain("TraceCapture", code);
    }

    [Fact]
    public void PluginTestNames_AreMessageAndStage_NumberedOnRepeat()
    {
        string[] names = ScaffoldTemplates.PluginTestNames(
        [
            new ScaffoldTemplates.PluginTestStep("Update", "UpdateRequest", [], "PreOperation"),
            new ScaffoldTemplates.PluginTestStep("Update", "UpdateRequest", [], "PostOperation"),
            new ScaffoldTemplates.PluginTestStep("Update", "UpdateRequest", [], "PostOperation"),
            new ScaffoldTemplates.PluginTestStep("Delete", "DeleteRequest", []),
        ]);

        Assert.Equal(["Update_PreOperation", "Update_PostOperation", "Update_PostOperation_2", "Delete"], names);
    }

    [Fact]
    public void AddCustomApiTestFile_ExecutesViaForMessage()
    {
        string code = ScaffoldTemplates.AddCustomApiTestFile(
            "Acme.Plugins.Tests.GlobalCustomApis", "Acme.Plugins.GlobalCustomApis",
            "RecalculateTotalsTests", "RecalculateTotals", "acme_RecalculateTotals");

        Assert.Contains("namespace Acme.Plugins.Tests.GlobalCustomApis", code);
        Assert.Contains("using Acme.Plugins.GlobalCustomApis;", code);
        Assert.Contains("PluginContext.ForMessage(\"acme_RecalculateTotals\")", code);
        Assert.Contains("new RecalculateTotals(string.Empty, string.Empty)", code);
    }

    [Fact]
    public void ContactDuplicateService_IsTableScoped_InPluginsProject_AndReads()
    {
        string service = ScaffoldTemplates.ContactDuplicateService("Zava");
        string plugin = ScaffoldTemplates.ContactRouterPlugin("Zava");

        Assert.Contains("namespace Zava.Plugins.Contact.Services", service);
        Assert.Contains("interface IContactDuplicateService", service);
        // It reads from Dataverse rather than writing, via the typed query over the early-bound Contact.
        Assert.Contains("Query.For<Contact>()", service);
        Assert.Contains(".Where(c => c.EMailAddress1, ConditionOperator.Equal, email)", service);

        // The router plugin injects the table-scoped service.
        Assert.Contains("GetRequiredService<IContactDuplicateService>", plugin);
    }

    [Fact]
    public void IntegrationsContactPlugin_UsesOnlySharedService()
    {
        string plugin = ScaffoldTemplates.ContactSyncPlugin("Zava");

        Assert.Contains("namespace Zava.Integrations.Contact.Plugins", plugin);
        Assert.Contains("GetRequiredService<IGreetingService>", plugin);
        // No fake "outbound sync" service in the integration package.
        Assert.DoesNotContain("OutboundSync", plugin);
    }

    [Fact]
    public void GeneratedCode_UsesFolderDerivedNamespaces()
    {
        Assert.Contains("namespace Zava.Plugins.Contact.Plugins", ScaffoldTemplates.ContactRouterPlugin("Zava"));
        Assert.Contains("namespace Zava.Plugins.Account.CustomApis", ScaffoldTemplates.AccountEstimateValueCustomApi("Zava"));
        Assert.Contains("namespace Zava.Plugins.GlobalCustomApis", ScaffoldTemplates.PingCustomApi("Zava"));
        Assert.Contains("namespace Zava.Integrations.Account.Plugins", ScaffoldTemplates.AccountSyncRouterPlugin("Zava"));
        Assert.Contains("namespace Zava.Integrations.Contact.CustomApis", ScaffoldTemplates.ContactExportCustomApi("Zava"));
        Assert.Contains("namespace Zava.Integrations.GlobalCustomApis", ScaffoldTemplates.IntegrationHeartbeatCustomApi("Zava"));
    }

    [Fact]
    public void RouterPlugin_BindsHandlerOnEveryStep()
    {
        string code = ScaffoldTemplates.ContactRouterPlugin("Zava");

        Assert.Equal(2, CountOccurrences(code, ".Handle("));
        Assert.Contains("OnCreatePost", code);
        Assert.Contains("OnUpdatePost", code);
    }

    [Fact]
    public void Samples_DoNotTeachRiskyPatterns()
    {
        // #444: newcomers copy the samples, so they must not cache privilege-filtered reads in the org-wide cache,
        // count a page of rows, trace personal data or retrieve every column.
        string relatedContacts = ScaffoldTemplates.AccountRelatedContactsPlugin("Zava");
        Assert.DoesNotContain("IPluginCacheService", relatedContacts);
        Assert.DoesNotContain(".Entities.Count", relatedContacts);
        Assert.Contains(".Count(c => c.ContactId, \"contactcount\")", relatedContacts);
        Assert.Contains(".ToFetchXml()", relatedContacts);

        string router = ScaffoldTemplates.ContactRouterPlugin("Zava");
        string sync = ScaffoldTemplates.AccountSyncRouterPlugin("Zava");
        string contactSync = ScaffoldTemplates.ContactSyncPlugin("Zava");
        foreach (string personal in new[] { "postImage.FirstName", "postImage.LastName", "preImage.LastName, ", "postImage.Name)", "preImage.Name)" })
        {
            Assert.DoesNotContain(personal, router);
            Assert.DoesNotContain(personal, sync);
        }

        Assert.DoesNotContain("ctx.Trace(greetings.BuildGreeting", contactSync);

        // PR #484 review: the message reference traces only ids, never a column value of the record.
        string messageReference = ScaffoldTemplates.AccountMessageReference("Zava");
        Assert.DoesNotContain("Target.Name", messageReference);
        Assert.DoesNotMatch(@"ctx\.Request\.Target\.(?!Id\b)\w+", messageReference);

        foreach (string tests in new[] { ScaffoldTemplates.SecurityExampleTests("Zava"), ScaffoldTemplates.AccountPostCreatePluginTests("Zava") })
        {
            Assert.DoesNotContain("new ColumnSet(true)", tests);
        }

        // One test drives the plugin through PluginPipeline, so a wrong FilterOn fails; the router test seeds the new
        // contact, so the duplicate query's self-exclusion is exercised.
        Assert.Contains("PluginPipeline.Empty().Register<AccountRelatedContactsPlugin>()", ScaffoldTemplates.AccountRelatedContactsPluginTests("Zava"));
        Assert.Contains("Seed(\n                created,", ScaffoldTemplates.ContactRouterPluginTests("Zava").Replace("\r\n", "\n"));
    }

    [Fact]
    public void BoundCustomApi_IsBoundToEntity()
    {
        string code = ScaffoldTemplates.AccountEstimateValueCustomApi("Zava");

        Assert.Contains("class AccountEstimateValueCustomApi", code);
        Assert.Contains("BoundTo<Account>()", code);
        Assert.Contains("public const string UniqueName = \"zava_AccountEstimateValue\";", code);
        Assert.Contains("RegisterCustomApi(UniqueName)", code);
    }

    [Fact]
    public void ContactValidateCustomApi_IsBoundToContact_InPluginsProject()
    {
        string code = ScaffoldTemplates.ContactValidateCustomApi("Zava");

        Assert.Contains("namespace Zava.Plugins.Contact.CustomApis", code);
        Assert.Contains("class ContactValidateCustomApi", code);
        Assert.Contains("BoundTo<Contact>()", code);
        Assert.Contains("public const string UniqueName = \"zava_ContactValidate\";", code);
        Assert.Contains("RegisterCustomApi(UniqueName)", code);
    }

    [Fact]
    public void AccountReconcileCustomApi_IsBoundToAccount_InIntegrationsProject()
    {
        string code = ScaffoldTemplates.AccountReconcileCustomApi("Zava");

        Assert.Contains("namespace Zava.Integrations.Account.CustomApis", code);
        Assert.Contains("class AccountReconcileCustomApi", code);
        Assert.Contains("BoundTo<Account>()", code);
        Assert.Contains("public const string UniqueName = \"zava_AccountReconcile\";", code);
        Assert.Contains("RegisterCustomApi(UniqueName)", code);
    }

    [Fact]
    public void IntegrationsTests_TargetTheIntegrationsCustomApis()
    {
        string reconcile = ScaffoldTemplates.AccountReconcileCustomApiTests("Zava");
        string export = ScaffoldTemplates.ContactExportCustomApiTests("Zava");
        string heartbeat = ScaffoldTemplates.IntegrationHeartbeatCustomApiTests("Zava");

        Assert.Contains("namespace Zava.Integrations.Tests.Account.CustomApis", reconcile);
        Assert.Contains("using Zava.Integrations.Account.CustomApis;", reconcile);
        Assert.Contains("namespace Zava.Integrations.Tests.Contact.CustomApis", export);
        Assert.Contains("namespace Zava.Integrations.Tests.GlobalCustomApis", heartbeat);
        Assert.Contains("\"healthy\"", heartbeat);
    }

    [Fact]
    public void GlobalCustomApi_IsNotBound()
    {
        string code = ScaffoldTemplates.PingCustomApi("Zava");

        Assert.Contains("class PingCustomApi", code);
        Assert.Contains("public const string UniqueName = \"zava_Ping\";", code);
        Assert.Contains("RegisterCustomApi(UniqueName", code);
        Assert.DoesNotContain("BoundTo", code);
    }

    [Fact]
    public void Models_UseEarlyBoundAttributes()
    {
        string account = ScaffoldTemplates.AccountModel("Zava");
        string contact = ScaffoldTemplates.ContactModel("Zava");

        Assert.Contains("[EntityLogicalName(EntityLogicalNameValue)]", account);
        Assert.Contains("[AttributeLogicalName(\"name\")]", account);
        Assert.Contains("[EntityLogicalName(EntityLogicalNameValue)]", contact);
        Assert.Contains("namespace Zava.Shared.Models.Generated", account);
    }

    [Fact]
    public void Solution_ReferencesEveryProject()
    {
        var projects = new[]
        {
            new ScaffoldTemplates.ProjectEntry("Zava.Shared", "Zava.Shared\\Zava.Shared.csproj", ScaffoldTemplates.StableGuid("Zava.Shared")),
            new ScaffoldTemplates.ProjectEntry("Zava.Plugins", "Zava.Plugins\\Zava.Plugins.csproj", ScaffoldTemplates.StableGuid("Zava.Plugins")),
        };

        string sln = ScaffoldTemplates.Solution(projects);

        Assert.Contains("Zava.Shared\\Zava.Shared.csproj", sln);
        Assert.Contains("Zava.Plugins\\Zava.Plugins.csproj", sln);
        Assert.Contains("Microsoft Visual Studio Solution File", sln);
    }

    [Fact]
    public void StableGuid_IsDeterministic()
    {
        Assert.Equal(ScaffoldTemplates.StableGuid("Zava.Plugins"), ScaffoldTemplates.StableGuid("Zava.Plugins"));
        Assert.NotEqual(ScaffoldTemplates.StableGuid("Zava.Plugins"), ScaffoldTemplates.StableGuid("Zava.Shared"));
    }

    [Fact]
    public void RootReadme_OmitsConsoleAndIntegrations_WhenDisabled()
    {
        ScaffoldOptions options = Options() with { IncludeConsole = false, IncludeIntegrations = false };

        string readme = ScaffoldTemplates.RootReadme(options);

        Assert.DoesNotContain("ConsoleApp", readme);
        Assert.DoesNotContain("Integrations", readme);
        // Signing is Integrations-only, so the section is gone too.
        Assert.DoesNotContain("## Signing the plugin package", readme);
    }

    [Fact]
    public void RootReadme_Samples_TellsTheTruthAboutTheSamples()   // A-50, A-78
    {
        string readme = ScaffoldTemplates.RootReadme(Options());

        // Replacing the stub models needs --overwrite (vela models refuses to overwrite otherwise).
        Assert.Contains("  --project src/Zava.Shared/Zava.Shared.csproj `\n  --overwrite\n", readme.Replace("\r\n", "\n"));
        // The related-contacts sample runs an aggregate FetchXML query from the typed builder.
        Assert.DoesNotContain("typed FetchXML builder", readme);
        Assert.Contains("`Query.For<Contact>().Count(...).ToFetchXml()`", readme);
        // The pipeline example passes secrets by variable name, not on the command line.
        string signed = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = true });
        Assert.Contains("--pfx-base64-env CODE_SIGN_PFX_BASE64 --pfx-password-env CODE_SIGN_PFX_PASSWORD", signed);
        Assert.DoesNotContain("--pfx-password $env:", signed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RootReadme_EveryPlanAndApplyExample_PassesSolution(bool samples)   // plan exits 4 on an unprefixed package
    {
        string readme = ScaffoldTemplates.RootReadme(Options() with { IncludeSamples = samples });

        string[] commands = System.Text.RegularExpressions.Regex.Matches(readme, @"vela (?:plan|apply) --package[^`\n]*")
            .Select(m => m.Value)
            .ToArray();
        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Contains("--solution ZavaSales", command));
    }

    [Fact]
    public void AccountPostCreatePlugin_DescribesTheCurrentUserService()
    {
        string code = ScaffoldTemplates.AccountPostCreatePlugin("Zava");
        Assert.DoesNotContain("initiating", code);
        Assert.Contains("acts as the current user", code);
    }

    [Fact]
    public void Samples_DescribeHandle_NotHandler()   // A-78
    {
        Assert.DoesNotContain("Handler(", ScaffoldTemplates.ContactRouterPlugin("Zava"));
    }

    [Fact]
    public void RootReadme_SigningSection_ReflectsManagedIdentityFlag()
    {
        string signed = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = true });
        Assert.Contains("## Signing the plugin package", signed);
        Assert.Contains("SignPluginPackage", signed);
        Assert.Contains("vela sign --package", signed);

        string unsigned = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = false });
        Assert.Contains("## Signing the plugin package", unsigned);
        Assert.Contains("vela init <name> --samples --managed-identity --force", unsigned);
        Assert.DoesNotContain("SignPluginPackage", unsigned);
    }

    [Fact]
    public void RootReadme_PackagesWithBuild_NotPack()
    {
        // The plugin package is produced by `dotnet build -c Release` (GeneratePackageOnBuild + PackageOutputPath),
        // never `dotnet pack` (which, run cold, emits an assembly-less package rejected by `vela plan`).
        string readme = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = true });

        Assert.Contains("dotnet build", readme);
        // No `dotnet pack <project>` command anywhere (prose may still mention it as the thing NOT to run).
        Assert.DoesNotContain("dotnet pack src", readme);
        Assert.DoesNotContain("dotnet pack .", readme);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
