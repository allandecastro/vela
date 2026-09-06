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
        Interactive: false);

    [Fact]
    public void ApiPrefix_IsLowerCasedAndDotless()
    {
        Assert.Equal("zavacrm", ScaffoldTemplates.ApiPrefix("Zava.CRM"));
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
        Assert.Contains("net462", csproj);
    }

    [Fact]
    public void SharedCsproj_ReferencesDataAnnotations_SoGeneratedModelsCompileOnNet462()
    {
        string csproj = ScaffoldTemplates.SharedCsproj(Options());

        // `vela models` emits [StringLength]/[Range] DataAnnotations by default; net462 must reference the
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
        Assert.Contains("--fingerprint $(PluginSigningFingerprint)", signed);
        Assert.Contains("--pfx-credential $(PluginSigningCredential)", signed);
        Assert.Contains("--pfx-password $(PluginSigningPassword)", signed);

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
        // Single-step plugins do not bind a handler.
        Assert.DoesNotContain(".Handler(", code);
    }

    [Fact]
    public void MessageReference_ShowcasesEveryTypedMessage_AsDisabledSteps()
    {
        string code = ScaffoldTemplates.AccountMessageReference("Zava");

        // One typed handler per modelled message shown on account (CRUD/bulk, relationships, merge, sharing,
        // and the account-valid sharing reads).
        string[] contexts =
        {
            "ICreateContext<Account>", "IUpdateContext<Account>", "IDeleteContext<Account>",
            "ICreateMultipleContext<Account>", "IUpdateMultipleContext<Account>",
            "IRetrieveContext<Account>", "IRetrieveMultipleContext<Account>",
            "IAssociateContext<Account>", "IDisassociateContext<Account>", "IMergeContext<Account>",
            "IGrantAccessContext<Account>", "IModifyAccessContext<Account>", "IRevokeAccessContext<Account>",
            "IRetrievePrincipalAccessContext<Account>", "IRetrieveSharedPrincipalsAndAccessContext<Account>",
        };
        foreach (string ctxType in contexts)
        {
            Assert.Contains(ctxType, code);
        }

        // Typed accessors are showcased rather than raw casts.
        Assert.Contains("ctx.Target", code);
        Assert.Contains("ctx.TargetReference", code);
        Assert.Contains("ctx.TargetCollection", code);
        Assert.DoesNotContain(".ToEntity<", code);

        // Every step is dormant: each registration ends with .Disabled(); (15), and every step binds a handler.
        Assert.Equal(15, CountOccurrences(code, ".Disabled();"));
        Assert.Equal(15, CountOccurrences(code, ".Handler("));
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
        // Table-scoped services take the initiating-user IOrganizationService via a primary constructor.
        Assert.Contains("private readonly IOrganizationService _service = service;", code);
        Assert.Contains("void Execute(Account target)", code);
    }

    [Fact]
    public void AddPluginTestFile_TargetsThePluginAndExecutesIt()
    {
        string code = ScaffoldTemplates.AddPluginTestFile(
            "Acme.Plugins.Tests.Account.Plugins", "Acme.Plugins.Account.Plugins", "Acme.Shared.Models.Generated",
            "AccountValidatorPluginTests", "AccountValidatorPlugin", "Account");

        Assert.Contains("namespace Acme.Plugins.Tests.Account.Plugins", code);
        Assert.Contains("using Acme.Plugins.Account.Plugins;", code);
        Assert.Contains("public sealed class AccountValidatorPluginTests", code);
        Assert.Contains("PluginContext.For<Account>()", code);
        Assert.Contains("new AccountValidatorPlugin(string.Empty, string.Empty)", code);
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

        Assert.Equal(2, CountOccurrences(code, ".Handler("));
        Assert.Contains("OnCreatePost", code);
        Assert.Contains("OnUpdatePost", code);
    }

    [Fact]
    public void BoundCustomApi_IsBoundToEntity()
    {
        string code = ScaffoldTemplates.AccountEstimateValueCustomApi("Zava");

        Assert.Contains("class AccountEstimateValueCustomApi", code);
        Assert.Contains("BoundTo<Account>()", code);
        Assert.Contains("RegisterCustomApi(\"zava_AccountEstimateValue\")", code);
    }

    [Fact]
    public void ContactValidateCustomApi_IsBoundToContact_InPluginsProject()
    {
        string code = ScaffoldTemplates.ContactValidateCustomApi("Zava");

        Assert.Contains("namespace Zava.Plugins.Contact.CustomApis", code);
        Assert.Contains("class ContactValidateCustomApi", code);
        Assert.Contains("BoundTo<Contact>()", code);
        Assert.Contains("RegisterCustomApi(\"zava_ContactValidate\")", code);
    }

    [Fact]
    public void AccountReconcileCustomApi_IsBoundToAccount_InIntegrationsProject()
    {
        string code = ScaffoldTemplates.AccountReconcileCustomApi("Zava");

        Assert.Contains("namespace Zava.Integrations.Account.CustomApis", code);
        Assert.Contains("class AccountReconcileCustomApi", code);
        Assert.Contains("BoundTo<Account>()", code);
        Assert.Contains("RegisterCustomApi(\"zava_AccountReconcile\")", code);
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
        Assert.Contains("RegisterCustomApi(\"zava_Ping\"", code);
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
    public void RootReadme_SigningSection_ReflectsManagedIdentityFlag()
    {
        string signed = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = true });
        Assert.Contains("## Signing the plugin package", signed);
        Assert.Contains("SignPluginPackage", signed);
        Assert.Contains("vela sign --package", signed);

        string unsigned = ScaffoldTemplates.RootReadme(Options() with { ManagedIdentity = false });
        Assert.Contains("## Signing the plugin package", unsigned);
        Assert.Contains("vela init <name> --managed-identity", unsigned);
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
