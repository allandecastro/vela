namespace Vela.Cli;

internal static partial class ScaffoldTemplates
{
    /// <summary>
    /// The AGENTS.md that <c>vela init</c> writes at the solution root: how an AI coding agent writes a step, a Custom
    /// API and a test in this solution, and drives the CLI. The C# snippets are compiled and run as
    /// <c>docs/samples/Vela.DocSamples/AgentsGuideSamples.cs</c> in the Vela repository; keep the two in sync.
    /// </summary>
    public static string AgentsGuide(ScaffoldOptions options)
    {
        string prefix = options.Prefix;
        string testsLine = options.IncludeTests
            ? $"- `tests/{prefix}.Plugins.Tests`: xUnit tests on the in-memory harness (`Vela.Testing`), no Dataverse connection.\n"
            : string.Empty;
        string testCommand = options.IncludeTests
            ? $"dotnet test tests/{prefix}.Plugins.Tests -c Release   # net48: on macOS/Linux run the test dll with Mono + xunit.console instead\n"
            : string.Empty;
        // With --no-tests the solution has no test project and no Vela.Testing reference, so the guide names neither.
        string packages = options.IncludeTests ? "`Vela.Plugins`, `Vela.Testing` and the `vela` CLI" : "`Vela.Plugins` and the `vela` CLI";
        // Two packages to two solutions: the Integrations package when there is one, else a second package to come.
        string secondPackage = options.IncludeIntegrations ? options.IntegrationsPackageId : "<OtherPackage>";
        string testsNamespace = options.IncludeTests ? $", its tests `{prefix}.Plugins.Tests.Account.Plugins`" : string.Empty;
        string testSection = options.IncludeTests ? "\n" + TestSection.Replace("{{prefix}}", prefix) : string.Empty;

        return
$$"""
# AGENTS.md

Instructions for AI coding agents in this Dataverse solution, scaffolded by `vela init`. It uses Vela
({{packages}}). Docs: https://allandecastro.github.io/vela/

## Layout

- `src/{{prefix}}.Shared`: early-bound models (`Models/Generated`, regenerate with `vela models`) and shared services.
- `src/{{prefix}}.Plugins`: plugins and Custom APIs. Targets .NET Framework 4.8, the version Microsoft supports
  (never net481). Plugin packages above net471 register only on Dataverse 9.2.26092 or later; on an older
  environment, drop to net471. The Release build packs a plugin package into `artifacts/`.
{{testsLine}}
## Commands

```bash
dotnet build {{options.SolutionName}}.sln -c Release   # build, analyzers, and the plugin package in artifacts/
{{testCommand}}vela models --entities account,contact --project src/{{prefix}}.Shared/{{prefix}}.Shared.csproj --interactive
vela add plugin AccountGuard --entity Account --message Update --stage PreOperation --filtering-attributes Name --project src/{{prefix}}.Plugins/{{prefix}}.Plugins.csproj
vela add customapi EstimateValue --global --function --prefix <publisherPrefix> --project src/{{prefix}}.Plugins/{{prefix}}.Plugins.csproj
vela plan  --packages-dir artifacts --solution <solution> --json   # read-only; JSON with "schemaVersion": 1
vela apply --packages-dir artifacts --solution <solution>          # writes; asks for confirmation - run only when the user asks
vela help --json                                                   # every command, option and exit code
```

Project values live in `PROJECT.md`; pass them as flags, do not ask for them again: `--solution` on `vela plan`
and `vela apply` (the solution of each package, under `packages`), `--prefix` on `vela add customapi`
(`publisherPrefix`). Check a solution's prefix with `vela solution show <solution> --json`. One plan or apply
targets one solution: packages that target different solutions take one run per solution, each with its `-p`:

```bash
vela plan -p artifacts/{{options.PluginsPackageId}}.1.0.0.nupkg --solution <solution of {{options.PluginsPackageId}}> --json
vela plan -p artifacts/{{secondPackage}}.1.0.0.nupkg --solution <solution of {{secondPackage}}> --json
```

Run `vela plan` and read its result before any `vela apply`, and never apply without being asked to.

## Code conventions

- Namespaces follow the folders: `src/{{prefix}}.Plugins/Account/Plugins/` is `{{prefix}}.Plugins.Account.Plugins`{{testsNamespace}}.
  Put the `using` directives inside a block `namespace { }`, as below, never above the namespace or with a
  file-scoped one: there, the `Account` namespace segment hides the `Account` model type (error CS0118).
  `.editorconfig` makes the IDE add usings inside, and the build warns (IDE0065) on one outside. In the root
  namespace `{{prefix}}.Plugins` itself, `Account` is always the namespace: keep table code in its table's folder.
- Name a handler after the behaviour (`OnCreditLimitChanging`, not `OnUpdate`); `vela add plugin` and
  `vela add step` take `--handler <Method>`.
- Project-wide services and message sources go in the generated `ProjectServices.cs` (an `IServiceRegistrar` with
  `[assembly: VelaServiceRegistrar]`), the message keys in `Messages.cs`:
  https://allandecastro.github.io/vela/guides/localization.html
- Give every public type a `/// <summary>`: the projects generate their XML documentation.

## Write a step

```csharp
namespace {{prefix}}.Plugins.Account.Plugins
{
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Guards and reviews changes to an account's credit limit.</summary>
    public sealed partial class AccountCreditPlugin(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.CreditLimit)          // required on Create/Update (VELA020)
                .WithPreImage(a => a.CreditLimit)      // only the columns you read
                .Handle(OnCreditLimitChanging);

            RegisterStep().OnUpdate().PostOperation().Async()
                .FilterOn(a => a.CreditLimit)
                .WithStepKey("credit-review")          // needed only when two steps on one trigger look identical
                .Handle(OnCreditLimitChanged);
        }

        // OnCreditLimitChangingContext is generated (the class must be partial): PreImage holds the captured columns.
        private void OnCreditLimitChanging(OnCreditLimitChangingContext ctx)
        {
            decimal before = ctx.PreImage.CreditLimit?.Value ?? 0m;
            decimal after = ctx.Request.Target.CreditLimit?.Value ?? 0m;
            if (before > 0 && after > before * 2)
            {
                throw new InvalidPluginExecutionException("The credit limit cannot more than double at once.");
            }
        }

        // An async step runs after the commit: write through a service, never to the Target (VELA018).
        private void OnCreditLimitChanged(OnCreditLimitChangedContext ctx) =>
            ctx.Service.Update(new Account { Id = ctx.PrimaryEntityId, Description = "Credit limit reviewed." });
    }
}
```

- Use the typed trigger (`OnCreate()`, `OnUpdate()`, `OnDelete()`, `OnAssociate()`...), then the stage, then
  `Sync()` / `Async()` (async is PostOperation only), then modifiers, and end with `Handle(...)`. A message has one
  way in: `On<TRequest>()` is only for Custom APIs and messages without a typed trigger (VELA023 rejects
  `On<UpdateRequest>()`).
- One class can register several steps, even on the same trigger; give them different filters or images, or a
  `WithStepKey(...)`.
- Images: a post-image only at PostOperation, never a pre-image on Create. The analyzers reject the rest at build time.
- Services: `ctx.Service` (the step's user), `ctx.SystemService` (SYSTEM), `ctx.AsUser(userId)`.
- Keep plugins stateless (no mutable fields). Throw `InvalidPluginExecutionException` for a message the user sees.
- Secrets: `WithSecureConfigFrom("Name")`, with the value given at deploy time
  (`vela apply --secure-config Name=...` or `VELA_SECURE_CONFIG_Name`). Never put a secret in code.

## Write a Custom API

```csharp
namespace {{prefix}}.Plugins.GlobalCustomApis
{
    using System;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;

    /// <summary>Estimates the value of an account.</summary>
    public sealed class EstimateValueApi(string unsecureConfig, string secureConfig)
        : CustomApiBase<EstimateValueRequest, EstimateValueResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{options.EffectivePublisherPrefix}}_EstimateValue")
                .DisplayName("Estimate value")
                .Description("Estimates the value of an account.")
                .AsFunction()
                .Request(r => r
                    .Add(x => x.AccountId)                                       // required by default
                    .Add(x => x.Discount, optional: true, "Discount to apply."))
                .Response(r => r.Add(x => x.Value));                             // a function needs a response
        }

        protected override EstimateValueResponse Execute(ILocalPluginContext ctx, EstimateValueRequest request) =>
            new EstimateValueResponse { Value = 1000m - request.Discount };
    }

    /// <summary>The request of the EstimateValue Custom API.</summary>
    public sealed class EstimateValueRequest : CustomApiRequest
    {
        public EstimateValueRequest() : base("{{options.EffectivePublisherPrefix}}_EstimateValue") { }

        public Guid AccountId { get => Get<Guid>(); set => Set(value); }

        public decimal Discount { get => Get<decimal>(); set => Set(value); }
    }

    /// <summary>The response of the EstimateValue Custom API.</summary>
    public sealed class EstimateValueResponse : CustomApiResponse
    {
        public EstimateValueResponse() : base("{{options.EffectivePublisherPrefix}}_EstimateValue") { }

        public decimal Value { get => Get<decimal>(); set => Set(value); }
    }
}
```

The unique name carries the publisher prefix. Parameter names are plain identifiers (letters, digits,
underscores). `Build()` rejects what Dataverse refuses, such as a function without a response property.
{{testSection}}
""";
    }

    // The AGENTS.md "Write a test" section, written only when the scaffold has a test project.
    private const string TestSection =
""""
## Write a test

Test with `PluginPipeline` by default; use `PluginContext` only for edge cases the pipeline does not easily produce
(a `Depth` of 2 or more, a crafted `ParentContext`, shared variables set by another plugin, an invalid Custom API
input, one error branch of a handler).

```csharp
namespace {{prefix}}.Plugins.Tests.Account.Plugins
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Tests of <see cref="AccountCreditPlugin"/>.</summary>
    public sealed class AccountCreditPluginTests
    {
        [Fact]
        public void Rejects_A_Credit_Limit_That_More_Than_Doubles()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountCreditPlugin>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava", CreditLimit = new Money(100m) });

            Assert.Throws<InvalidPluginExecutionException>(() =>
                pipeline.Service.Update(new Account { Id = id, CreditLimit = new Money(500m) }));
        }
    }
}
```

```csharp
namespace {{prefix}}.Plugins.Tests.GlobalCustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.GlobalCustomApis;

    /// <summary>Tests of <see cref="EstimateValueApi"/>.</summary>
    public sealed class EstimateValueApiTests
    {
        [Fact]
        public void Estimates_A_Value()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<EstimateValueApi>();

            EstimateValueResponse response = pipeline.Service
                .Execute(new EstimateValueRequest { AccountId = Guid.NewGuid(), Discount = 100m })
                .ToResponse<EstimateValueResponse>();

            Assert.Equal(900m, response.Value);
        }
    }
}
```

`PluginPipeline` runs every registered step around each request, as Dataverse does (stages, filters, images,
transactions, async steps after the commit). Read stored rows with `pipeline.GetAll("account")`. For an edge
case, `PluginContext.For<Account>().WithTarget(...).Execute<AccountCreditPlugin>()` runs one handler directly.
"""";
}
