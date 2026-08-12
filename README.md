<p align="center">
  <img src="branding/vela-chevron.svg" alt="Vela" width="120" height="120" />
</p>

<h1 align="center">Vela</h1>

<p align="center">
  <strong>Build Microsoft Dataverse with confidence — fluent plugins &amp; Custom APIs, a preview-first <code>plan</code> → <code>apply</code> CLI, and a no-connection test harness.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/license-MIT-green?style=for-the-badge" alt="License: MIT" />
  <a href="https://github.com/allandecastro/vela/actions/workflows/ci.yml?query=branch%3Amain"><img src="https://github.com/allandecastro/vela/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/mutation.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/mutation.yml/badge.svg" alt="Mutation Testing" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/release.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/.NET_Framework_4.6.2-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET Framework 4.6.2" />
  <img src="https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Microsoft_Dataverse-107C10?style=for-the-badge&logo=microsoft&logoColor=white" alt="Microsoft Dataverse" />
  <img src="https://img.shields.io/badge/Power_Platform-742774?style=for-the-badge&logo=microsoftpowerplatform&logoColor=white" alt="Power Platform" />
</p>

Vela is a framework (`Vela.Plugins`), a CLI (`Vela.Cli`, command `vela`) and a no-connection test harness
(`Vela.Testing`) for building Microsoft Dataverse plugins and Custom APIs, with a safe, preview-first
`plan` → `apply` workflow.

## Features

- **Fluent base types** — declarative step registration and strongly-typed handlers for plugins and Custom APIs.
- **Typed context helpers** — strongly-typed target/images, framework tracing, lightweight DI, and a TTL cache.
- **`vela` CLI** — preview-first `plan` → `apply` of registrations, scaffolding (`vela init` / `vela add`), early-bound generation (`vela models`), and package signing.
- **Idempotent apply** — safe upsert/delete of steps, Custom APIs, and images against a live environment.
- **No-connection testing** — run plugins against an in-memory Dataverse with no live connection and no mocking-library dependency, with an **opt-in security model** (roles, privileges, business units, teams, sharing and relationship cascades) when you need it.

## Packages

| Package | Target | What it gives you |
| --- | --- | --- |
| `Vela.Plugins` | .NET Framework 4.6.2 | Runtime: fluent plugin/Custom API base types, typed context helpers, lightweight DI and caching. |
| `Vela.Cli` (command `vela`) | .NET 10.0 | Plan/apply, scaffolding and package signing for plugins and Custom APIs. |
| `Vela.Testing` | .NET Framework 4.6.2 | Test harness: fake execution context + in-memory `IOrganizationService`, fluent strongly-typed builder. |

> **Status** — Pre-1.0 and actively developed; **not yet on NuGet.org**, so build from source or consume via a local feed for now. Publishing is automated over two release lines — pushing a `framework-vX.Y.Z` tag publishes `Vela.Plugins` + `Vela.Testing`, and `cli-vX.Y.Z` publishes `Vela.Cli`. See [`CHANGELOG.md`](CHANGELOG.md) for what's shipped and the versioning policy.

## Table of contents

**Get started**
- [Quickstart](#quickstart)
- [The `vela` CLI](#the-vela-cli)
- [Scaffold a new solution (`vela init`)](#scaffold-a-new-solution-vela-init)
- [Add a plugin, service, Custom API, test or step (`vela add`)](#add-a-plugin-service-custom-api-test-or-step-vela-add)

**Write your code**
- [Write a plugin (Steps-first)](#write-a-plugin-steps-first)
- [Write a Custom API](#write-a-custom-api)
- [Dependency injection (lightweight DI)](#dependency-injection-lightweight-di)
- [Caching (TTL + refresh)](#caching-ttl--refresh)

**Test & ship**
- [Unit-test your plugins (`Vela.Testing`)](#unit-test-your-plugins-velatesting)
- [End-to-end example](#end-to-end-example)
- [Cookbook (common recipes)](#cookbook-common-recipes)
- [Troubleshooting](#troubleshooting)

**Reference (advanced)**
- [Sign the plugin package (`vela sign`)](#sign-the-plugin-package-vela-sign)
- [CLI automation](#cli-automation)
- [Registration manifest generation](#registration-manifest-generation)
- [Early-bound generation (`vela models`)](#early-bound-generation-vela-models)
- [Limitations & not supported](docs/LIMITATIONS.md)

- [Contributing](#contributing)
- [License](#license)

## Quickstart

From an empty folder to registered, running steps in five commands:

```powershell
# 1. Install the CLI (.NET global tool — exposes the `vela` command)
dotnet tool install --global Vela.Cli

# 2. Scaffold a complete, build-ready solution
vela init --name ZavaSales --prefix Zava
cd ZavaSales

# 3. Build the plugin package into a .nupkg
dotnet pack src/Zava.Plugins -c Release -o artifacts

# 4. Preview what would change in your environment (read-only)
vela plan  --packages-dir artifacts --interactive

# 5. Apply the changes (idempotent create/update/delete)
vela apply --packages-dir artifacts --interactive --execute
```

What you get from `vela init`: a solution that references `Vela.Plugins` as a NuGet package, with seeded
plugins, Custom APIs, services and a no-connection test project. From there:

- **Write your own code** — [plugins](#write-a-plugin-steps-first), [Custom APIs](#write-a-custom-api),
  [DI](#dependency-injection-lightweight-di), [caching](#caching-ttl--refresh).
- **Test without a connection** — [`Vela.Testing`](#unit-test-your-plugins-velatesting).
- **Ship** — `dotnet pack` → [`vela plan`](#cli-automation) → `vela apply --execute`.

> Prefer to add Vela to an **existing** plugin project? Reference the package directly instead of
> scaffolding: `dotnet add src/Zava.Plugins package Vela.Plugins`. The plugin package project
> (a `pac plugin init` project) references your domain/model projects, which in turn use the framework
> abstractions through the NuGet dependency.

## The `vela` CLI

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command.

Install from your NuGet feed (once published):

```powershell
dotnet tool install --global Vela.Cli
```

Or install from a locally built package:

```powershell
dotnet pack .\src\Vela.Cli -c Release -o .\artifacts\cli
dotnet tool install --global --add-source .\artifacts\cli Vela.Cli
```

Then run it (update later with `dotnet tool update --global Vela.Cli`):

```powershell
vela plan --packages-dir .\artifacts --interactive
```

Core commands:

| Command | What it does |
| --- | --- |
| `vela init` / `vela new` | [Scaffold](#scaffold-a-new-solution-vela-init) a complete, build-ready plugin solution. |
| `vela add` | [Generate a single file](#add-a-plugin-service-custom-api-test-or-step-vela-add) (plugin / service / customapi / test) into an existing project, or add a step to one. |
| `vela models` | [Generate early-bound classes](#early-bound-generation-vela-models) for a solution or table list over your connection. |
| `vela plan` | Preview the create/update/delete operations needed to converge Dataverse with your package (read-only). |
| `vela apply --execute` | Apply those operations (idempotent upsert/delete). See [CLI automation](#cli-automation). |
| `vela sign` | [Sign a plugin package](#sign-the-plugin-package-vela-sign) (required for managed-identity plugins). |

> **Secrets:** prefer the `DATAVERSE_CLIENT_SECRET` environment variable or `--client-secret-stdin`
> over `--client-secret`, which is exposed to shell history and process listings (the CLI warns when
> you use it). For sovereign clouds / custom app registrations under `--interactive`, override the
> MSAL login with `--auth-client-id` and `--authority`. Add `--verbose` for human-readable summaries
> and full exception stack traces on failure.

## Scaffold a new solution (`vela init`)

Generate a complete, build-ready plugin solution in one step:

```powershell
vela init --name ZavaSales --prefix Zava --output .
# or run the wizard:
vela init --interactive
```

This creates `ZavaSales/` with the `.sln` at the root and sibling `src/` and `tests/` folders:

```text
ZavaSales/
  ZavaSales.sln
  src/
    Zava.Shared/            # early-bound Account/Contact models + shared services (net462;net10.0)
    Zava.Plugins/           # plugin package -> builds a Dataverse plugin-package .nupkg
      Account/                 #   organised by table: Plugins/ Services/ CustomApis/
      Contact/
      GlobalCustomApis/        #   unbound Custom APIs
    Zava.Integrations/      # second plugin package (omit with --no-integrations)
    Zava.ConsoleApp/        # .NET 10 interactive browser login (omit with --no-console)
  tests/
    Zava.Plugins.Tests/      # xunit tests using Vela.Testing — no connection (omit with --no-tests)
    Zava.Integrations.Tests/ # xunit tests for the Integrations package
  Directory.Build.props        # shared build settings for src/ and tests/
  nuget.config                 # local-packages/ feed for Vela.Plugins / Vela.Testing
```

- `Zava.Shared` — early-bound `Account`/`Contact` models and shared services (multi-targets `net462;net10.0`).
- `Zava.Plugins` — plugin package that builds a Dataverse plugin-package `.nupkg` (via the
  `Microsoft.PowerApps.MSBuild.Plugin` SDK). Code is organised **by table** (`Account/`, `Contact/`)
  with `Plugins/`, `Services/` and `CustomApis/` subfolders, plus `GlobalCustomApis/` for unbound APIs;
  each file's namespace matches its folder. Seeded with a single-step plugin (schedules a follow-up
  task), a multi-step router plugin, table-**bound** + **global** Custom APIs, a cross-table shared
  service, and two **table-scoped** services (one write, one read).
- `Zava.Integrations` — a second plugin package (same layout, integration-themed names). Omit with `--no-integrations`.
- `Zava.Plugins.Tests` — a unit-test project (xunit) that runs the seeded plugins/Custom APIs with
  [`Vela.Testing`](#unit-test-your-plugins-velatesting) — no Dataverse connection. Run with
  `dotnet test tests/Zava.Plugins.Tests`. Omit with `--no-tests`.
- `Zava.Integrations.Tests` — the matching test project for the Integrations package (generated when both
  the Integrations project and tests are included).
- `Zava.ConsoleApp` — a .NET 10 console app that signs in to Dataverse **interactively** (browser).
  Omit with `--no-console`.

Flags: `--prefix` (default: derived from name), `--output` (default: `.`), `--core-version`
(default: `1.0.0`), `--no-integrations`, `--no-tests`, `--no-console`, `--managed-identity` / `--mi`
(wire an opt-in package-signing step into the Integrations project — see [Sign the plugin package](#sign-the-plugin-package-vela-sign)),
`--force` (overwrite an existing folder). The generated solution references `Vela.Plugins` as
a NuGet package, then follows the `dotnet pack` → `vela plan` → `vela apply` loop from the [Quickstart](#quickstart).

It also emits a `nuget.config` with a `local-packages/` folder feed, since `Vela.Plugins` (and
`Vela.Testing`, used by the test project) are **not on nuget.org**: point that source at your private
feed, or drop their `.nupkg` files into `local-packages/` (nuget.org is inherited for the Microsoft SDK
and test packages).

## Add a plugin, service, Custom API, test or step (`vela add`)

Once a solution exists, generate a **single** plugin, service, Custom API or test file into a project (or add a
step to an existing plugin) without writing the boilerplate by hand. Namespaces are inferred from the target project's `RootNamespace`, and files
follow the same by-table layout as `vela init` (so they drop straight into a scaffolded solution):

```powershell
# run inside the project folder, or pass --project <csproj>
vela add plugin AccountValidatorPlugin --entity Account --message Update
vela add service AccountFollowUpService --entity Account  # table-scoped interface + implementation
vela add customapi RecalculateTotals                  # global (unbound), lightweight Custom API
vela add customapi ExportContact --entity Contact     # typed Custom API bound to a table

# run inside the test project (RootNamespace ending in .Tests)
vela add test AccountValidatorPlugin --entity Account            # test for a plugin
vela add test RecalculateTotals --kind customapi                # test for a global Custom API

# add a step to an existing multi-step plugin (edits the file in place)
vela add step AccountRouterPlugin --entity Account --message Update

# or run the guided wizard (prompts for kind/name/entity/options)
vela add --interactive
```

- `--entity <ModelType>` is the early-bound class name (e.g. `Account`); it sets the `EntityPluginBase<T>`
  type and the `<Table>/` folder. Required for a plugin and a service; omit on a Custom API to generate a global one.
- **Custom API ergonomics:** `--function`/`--action` choose the operation type (default `Action`), and
  `--bound`/`--global` make the binding explicit instead of inferring it from `--entity` (`--bound` requires
  `--entity`; `--global` is unbound).
- `--message <Message>` (plugin, default `Create`): `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple`/
  `Associate`/`Disassociate`/`SetState` get a strongly-typed `On...()` call; any other name is registered as a
  custom SDK message via `OnMessage("...")`. `--project <csproj>` (default: the single `.csproj` in the current
  directory), `--force` (overwrite an existing file).
- `vela add service` scaffolds a table-scoped `I<Name>` interface + sealed implementation (injecting
  `IOrganizationService`) under `<Table>/Services/`, and prints the `services.AddScoped<...>(...)` line to drop
  into your plugin's `ConfigureServices(...)`.
- `vela add test <ClassUnderTest>` scaffolds a `Vela.Testing` test (`<ClassUnderTest>Tests`) pre-wired with
  `PluginContext.For<…>()…Execute(…)`. Run it inside the test project (its `RootNamespace` must end in `.Tests`);
  use `--kind customapi` for a Custom API test (and omit `--entity` for a global one).
- `vela add step <PluginName>` edits an existing **handler-based (multi-step)** plugin in place, appending a
  `RegisterSteps().On<Message>()…Handler(nameof(Handle<Message>))` registration and a matching handler method
  (`--message`, optional `--handler <Method>`). It refuses single-step plugins (those using the `Execute(...)`
  override) and any file it can't parse cleanly, so it never corrupts source.

## Write a plugin (Steps-first)

Create a class inheriting `EntityPluginBase<TEntity>` and declare steps in `RegisterPluginSteps()` with `RegisterSteps()`:

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.StepBuilder;
using Zava.Shared.Models;

namespace Zava.Plugins.Account
{
    public class AccountPostUpdate : EntityPluginBase<Account>
    {
        public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)      // Only trigger when Name changes
                .Description("Handles account updates")
                .PreImage()
                    .Attributes(a => a.Name)           // Include Name in pre-image
                .PostImage()
                    .Attributes(a => a.Name);          // Include Name in post-image
        }

        protected override void Execute(ILocalPluginContext ctx, Account target)
        {
            var preImage = ctx.GetPreImage<Account>();
            ctx.Trace($"Name changed from {preImage.Name} to {target.Name}");
        }
    }
}
```

You can register multiple steps in one plugin class and bind each step to a dedicated method:

```csharp
public class AccountStepRouter : EntityPluginBase<Account>
{
    public AccountStepRouter(string u, string s) : base(u, s) { }

    protected override void RegisterPluginSteps()
    {
        RegisterSteps().OnCreate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .Handler(OnCreatePost)
            .Description("Handles account creation after commit.")
            .PostImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1);

        RegisterSteps().OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)  // required on Create/Update
            .Handler(OnUpdatePost)
            .Description("Handles customer-facing account changes.")
            .PreImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1)
            .PostImage()
                .Attributes(a => a.Name, a => a.Telephone1, a => a.EmailAddress1);
    }

    private void OnCreatePost(ILocalPluginContext ctx, Account target)
    {
        var postImage = ctx.GetPostImage<Account>();
        ctx.Trace($"Account created: {postImage.Name} / {postImage.EmailAddress1}");
    }

    private void OnUpdatePost(ILocalPluginContext ctx, Account target)
    {
        var preImage = ctx.GetPreImage<Account>();
        var postImage = ctx.GetPostImage<Account>();
        ctx.Trace($"Account updated: {preImage.Name} -> {postImage.Name}");
    }
}
```

Each step in a multi-step plugin must have a **distinct trigger** (message + stage + mode + entity) and its own
`Handler(...)`. Two steps with the same trigger can't be told apart at runtime, so `Build()` rejects them — put
the shared logic in one step (combine the filtering attributes) or use a different stage/mode.

### Do / Don't (plugins)

**Do**
- Use `RegisterPluginSteps()` + `RegisterSteps().On...()` for single-step and multi-step plugins.
- Use `ctx.GetRequiredService<T>()` for dependency resolution in handlers.
- Use typed helpers (`GetTarget<T>`, `GetPreImage<T>`, `GetPostImage<T>`) instead of raw dictionaries.

**Don't**
- Start new plugin code on `RegisterPluginStep(IStepBuilder<TEntity>)` legacy shape.
- Cast context to `LocalPluginContext` in handlers.
- Mix multiple registration styles in the same plugin class.

### Step configuration flow

The fluent builder enforces a specific order:

1. **Message** - `OnCreate()`, `OnUpdate()`, `OnCreateMultiple()`, `OnUpdateMultiple()`, `OnDelete()`,
   `OnRetrieve()`, `OnRetrieveMultiple()`, `OnAssociate()`, `OnDisassociate()`, `OnSetState()`, or
   `OnMessage("custom_message")` for any other SDK message.
2. **Stage** - `Stage(PluginStages.PostOperation)`
3. **Mode** - `Mode(ExecutionMode.Synchronous)` or `Mode(ExecutionMode.Asynchronous)`
4. **Filtering** - `FilteringAttributes(a => a.Attr1, a => a.Attr2)` — **required for `Create`, `Update`,
   `CreateMultiple` and `UpdateMultiple`** (at least one attribute) and must come immediately after `Mode(...)`;
   not offered on other messages. An unfiltered Create/Update step would fire on every change, so the builder
   makes you state the trigger.
5. **Optional** - `Description()`, `ImpersonateUsing()`, `SupportedDeployment()`, `ExecutionOrder()`, `Disabled()`, `UnsecureConfig(...)`, `SecureConfig(...)`
6. **Images** - `PreImage().Attributes(...)` and/or `PostImage().Attributes(...)`

Short aliases are available when you prefer action-style naming: `AddPreImage(...)`, `AddPostImage(...)`.

**Target across message families** — `EntityPluginBase<T>` hands `Execute(ctx, target)` (and step handlers) a strongly-typed
target for every message: a full entity for `Create`/`Update`, an id-only entity built from the `EntityReference` for
`Delete`/`Associate`/`Disassociate`/`Retrieve` (and from the `EntityMoniker` for `SetState`), and an empty typed entity for
messages with no primary record (e.g. `RetrieveMultiple`) so the target is never null.

**Enabled / disabled** — steps are registered enabled by default. Mark a step `Disabled()` to register it dormant
(kill-switch or phased rollout); `apply` sets the step's state accordingly and `plan` shows the transition. Re-enabling
is just removing the `Disabled()` marker.

**Step configuration** — `UnsecureConfig("...")` and `SecureConfig("...")` declare the configuration strings passed to the
plugin constructor `(string unsecureConfig, string secureConfig)`. Both are reconciled by `apply` (the secure value is
stored on the linked `sdkmessageprocessingstepsecureconfig` row). Unsecure config appears verbatim in the `vela plan`
JSON (it is non-sensitive by design); the **secure** value is **redacted to a hash** in plan/inspect output (and any
`-o` plan file), so a changed secret is still detected without the value ever leaving Dataverse.

`MainOperation` is reserved for Custom API main handlers and is rejected for regular plugin-step fluent registrations.

Image validation rules enforced by the framework:

1. `PreValidation` - no images allowed.
2. `PreOperation` - only `PreImage`, only for `Update` and `Delete`.
3. `PostOperation` - `PreImage` for `Update`/`Delete`, `PostImage` for `Create`/`Update`.
4. `MainOperation` - not allowed for regular plugin steps.

### Context helpers

- `ctx.GetTarget<T>()` - Get the target entity
- `ctx.GetTargets()` / `ctx.TryGetTargets(...)` - Get bulk targets for `CreateMultiple` / `UpdateMultiple`
- `ctx.GetPreImage<T>()`, `ctx.GetPostImage<T>()` - Get images as early-bound
- `ctx.GetPreImage(index, alias)`, `ctx.GetPostImage(index, alias)` - Get per-record images in bulk operations
- `ctx.GetPreImageMerged()`, `ctx.GetPostImageMerged()` - Merge target with image
- `ctx.GetPreImageMerged(index)`, `ctx.GetPostImageMerged(index)` - Merge per-record target with per-record image in bulk operations
- `target.Contains(x => x.Name)` - Typed attribute-presence check (no magic strings). `Contains(x => x.A, x => x.B)` requires **all**; `ContainsAny(x => x.A, x => x.B)` needs **at least one** — useful to test which columns were in the `Target`
- `ctx.HasChanged<Account>(x => x.Name)` / `ctx.HasAnyChanged<Account>(x => x.A, x => x.B)` - True when the attribute is in the `Target` **and** differs from the registered pre-image (requires an entity `Target` + a pre-image; throws otherwise) — the "only act when X actually changed" guard
- `entity.GetFormattedValue(x => x.StatusCode)` - The formatted (display) value for a choice/lookup/money/date, or `null`
- `ctx.Tag` / `ctx.GetSharedVariable("key")` / `ctx.TryGetSharedVariable<T>("key", out var v)` - Read shared variables: the client-supplied `tag`, a value set by an earlier plugin in the same operation, or one inherited from the parent context of a cascaded operation
- `ctx.IsApplicationUser`, `ctx.EnvironmentId`, `ctx.TenantId`, `ctx.InitiatingUserAgent`, `ctx.IsPortalsClientCall`, `ctx.PortalsContactId` - Extended execution context helpers with graceful fallback
- `service.UploadFile(target, "vela_doc", "name.ext", bytes)` / `service.DownloadFile(target, "vela_doc")` / `service.DeleteFile(...)` - One-call File/Image column helpers (`using Vela.Files;`) that drive the block upload/download protocol for you
- `ctx.AcquireManagedIdentityToken(scopes)` - Acquire token via `IManagedIdentityService` when available
- `ctx.TraceMethod("details")` - Trace caller method name with correlation/stage/depth
- `ctx.TraceExecutionSnapshot("phase")` - Trace a structured context snapshot, including full Target/Image/Input/Output attribute values with PascalCase sections

Managed identity example:

```csharp
string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });
```

Dataverse operation tracing is automatic through wrapped services (`UserService`, `SystemService`, `CreateOrganizationService`): each call logs operation name (`Create`, `Update`, `RetrieveMultiple`, `Execute`, ...), timestamp, duration, and pipeline context.

When plugin telemetry is available in runtime (`Microsoft.Xrm.Sdk.PluginTelemetry.ILogger`), trace messages are also forwarded to plugin telemetry for Application Insights correlation.

Whether a trace is *persisted* is the Dataverse **Plugin Trace Log** environment setting (`Off` / `Exception` / `All`) — not anything in code. There is no framework-level verbosity switch: server-side plugins run in Microsoft's sandbox where OS environment variables cannot be set, so a runtime verbosity flag would have no effect online. What you control in code is *what extra detail* you trace, at the call site.

**You get two snapshots for free on every execution.** `PluginBase` wraps your handler and emits the
boundaries automatically — a `start` snapshot before your code runs, then `success` when it returns (or
`invalid-plugin-exception` / `business-exception` / `unhandled-exception` if it throws). You write nothing for
those; the phase names are fixed.

To capture **intermediate** state, call `TraceExecutionSnapshot(...)` yourself **inside your handler** with your
own phase label — a checkpoint around a risky step. (The framework already covers entry and exit, so there's no
point re-emitting `start` yourself.) Use the lighter `TraceMethod(...)` when you don't need the full payload:

```csharp
protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
{
    var account = ctx.GetTarget<Account>();

    ctx.TraceMethod("computing credit limit");       // lightweight: method/message/stage/depth, no payload
    var limit = ComputeCreditLimit(account);

    ctx.TraceExecutionSnapshot("after-credit-calc"); // full snapshot at this exact point, your own label
    ctx.UserService.Update(account);
}
```

> **Warning:** A snapshot dumps Target/Image/Input/Output attribute values into the Plugin Trace Log. Avoid calling `TraceExecutionSnapshot` from plugins handling sensitive data when the environment's Plugin Trace Log is set to `All`.

#### Tracing detail added by the framework

At runtime, the framework automatically adds:

1. Pipeline context on execution start/end (`message`, `stage`, `mode`, `depth`, `correlationId`, entity).
2. Structured execution snapshots (`TraceExecutionSnapshot(...)`), emitted automatically by `PluginBase` at the
   `start`, `success` and exception phases — so you rarely call it yourself.
3. Dataverse operation tracing on wrapped services with operation name + duration.
4. Optional plugin telemetry forwarding when Dataverse telemetry logger is available.

`TraceExecutionSnapshot(...)` always emits the **full** structured payload (every Input/Output parameter, the
pre/post images and the Target, with attribute values) — there is no compact/verbose mode:

```text
[2026-06-14T09:12:44.1573820Z] ExecutionSnapshot
  Phase: start
  MessageName: Update
  Stage: 20
  Mode: 0
  Depth: 1
  CorrelationId: 8f1c2d6e-4a7b-4f0e-9c11-2b6e1d2a9f33
  PrimaryEntityName: account
  PrimaryEntityId: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
  UserId: 6c2f1a8b-5d33-4e90-8a17-0f4b2c9d7e55
  InitiatingUserId: 6c2f1a8b-5d33-4e90-8a17-0f4b2c9d7e55
  IsApplicationUser: False
  IsPortalsClientCall: False
  PortalsContactId: <none>
  InitiatingUserAgent: <none>
  EnvironmentId: <none>
  TenantId: <none>
  IsBulkOperation: False
  InputParameters:
    Target:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        creditlimit: 1500 (Money)
        name: "Zava"
  OutputParameters:
    <none>
  PreEntityImages:
    PreImage:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        name: "Old Zava"
  PostEntityImages:
    PostImage:
      LogicalName: account
      Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
      Attributes:
        name: "Zava"
  Target:
    LogicalName: account
    Id: 3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
    Attributes:
      creditlimit: 1500 (Money)
      name: "Zava"
```

Example output — wrapped Dataverse operation tracing (automatic on `UserService` / `SystemService`), emitted as paired `start` / `ok` entries:

```text
[2026-06-14T09:12:44.1790021Z] Dataverse.Update start
  scope       : UserService
  message     : Update
  stage/mode  : 20/0
  depth       : 1
  correlation : 8f1c2d6e-4a7b-4f0e-9c11-2b6e1d2a9f33
  details     : entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
[2026-06-14T09:12:44.1820017Z] Dataverse.Update ok
  scope       : UserService
  durationMs  : 42
  details     : entity=account; id=3a0b9d52-1e44-4c8a-b1f0-7d2c5a9e6b41
```

Notes:

- Snapshots are always built in full; **whether they are persisted** is the environment's **Plugin Trace Log**
  setting (`Off` / `Exception` / `All`). There is **no framework verbosity switch** — a server-side plugin can't
  read OS environment variables, so a runtime flag would be inert. Keep the setting off `All` for plugins
  handling sensitive data (snapshots dump attribute values — see the warning above).
- Keep your own handler traces business-focused (decisions and key values); rely on the framework for the
  infra / snapshot / Dataverse-operation tracing.

### Typed QueryExpression helpers

Use typed extensions on `QueryExpression` to avoid string literals:

```csharp
using Vela.Querying;
using Zava.Shared.Models;
using Microsoft.Xrm.Sdk.Query;

QueryExpression query = new QueryExpression(Account.EntityLogicalName)
    .Select<Account>(x => x.AccountId, x => x.Name)
    .Where<Account>(x => x.Name, ConditionOperator.NotNull);
```

`Query.For<TEntity>()` returns a typed builder so the whole chain is typed **without repeating the entity type** (the entity name comes from `[EntityLogicalName]`), and it converts implicitly to `QueryExpression`:

```csharp
QueryExpression query = Query.For<Account>()
    .Select(a => a.AccountId, a => a.Name)
    .Where(a => a.Name, ConditionOperator.NotNull)
    .OrderBy(a => a.Name);
```

Links are typed too — give the target type once and configure the join (columns, criteria, nested links) through a typed action:

```csharp
QueryExpression query = Query.For<Contact>()
    .Where(c => c.FullName, ConditionOperator.BeginsWith, "J")
    .Link<Account>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.Inner, "acct",
        acct => acct.Select(a => a.Name)
                    .Where(a => a.StateCode, ConditionOperator.Equal, 0));
// crm.RetrieveMultiple(query) — the in-memory engine and Dataverse both accept it
```

Also available on the builder: `Distinct()`, `Page(n, count)`, `WithTotalRecordCount()`, `WhereColumns(a, op, b)`
(compare two columns), `WhereLink<TLinked>(alias, …)` (conditions on a join, e.g. records-not-in-a-set),
`WhereRelated<TTo>(…, JoinOperator.Any/All/NotAny/NotAll, …)`, and aggregates
`GroupBy(…)/Count/Sum/Average/Min/Max(…, alias)`.

## Write a Custom API

For Custom APIs, use `CustomApiBase<TRequest, TResponse>` and the fluent registration hook:

```csharp
public class CalculateAccountScore : CustomApiBase<ScoreRequest, ScoreResponse>
{
    public CalculateAccountScore(string u, string s) : base(u, s) { }

    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore")
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score")
            .AsAction()
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
            .WorkflowSdkStep(true)
            .Request(r => r.Add(x => x.AccountId))
            .Response(r => r.Add(x => x.Score));
    }

    protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
    {
        return new ScoreResponse { Score = ComputeScore(request.AccountId) };
    }
}

public class ScoreRequest { public Guid AccountId { get; set; } }
public class ScoreResponse { public int Score { get; set; } }
```

The generic base exposes `RegisterCustomApi(...)`, and the same shape is available from `Register.RegisterCustomApi<TRequest, TResponse>()` when you want to build manifests or scripts directly.

For small Custom APIs, you can also choose the lightweight mode and declare parameters directly without dedicated request/response CLR models:

```csharp
public class CalculateAccountScoreLight : CustomApiBase
{
    public CalculateAccountScoreLight(string u, string s) : base(u, s) { }

    protected override void RegisterCustomApi()
    {
        RegisterCustomApi("zava_CalculateAccountScore", ExecuteScore)
            .DisplayName("Calculate Account Score")
            .Description("Calculates an account score")
            .AsAction()
            .RequestParameter<Guid>("AccountId")
            .ResponseParameter<int>("Score")
            .ResponseParameter<string>("Rating");
    }

    private void ExecuteScore(ILocalPluginContext ctx)
    {
        Guid accountId = ctx.GetInputParameter<Guid>("AccountId");
        ctx.SetOutputParameter("Score", ComputeScore(accountId));
        ctx.SetOutputParameter("Rating", "High");
    }
}
```

Use the generic base when you want typed request/response models and automatic parameter mapping; use the lightweight base when you prefer fewer files and direct access to `InputParameters` / `OutputParameters`.

Both modes support the same direct pattern with `RegisterCustomApi(...)` as the registration entry point.

### Do / Don't (Custom APIs)

**Do**
- Override `RegisterCustomApi()` and start with `RegisterCustomApi(...)`.
- Keep metadata + handler binding in one fluent block.
- Prefer typed mode when request/response contracts are reused.

**Don't**
- Start new code with legacy `RegisterCustomApi(ICustomApiBuilder...)` overloads.
- Scatter metadata across multiple methods.
- Access raw `InputParameters` / `OutputParameters` when a typed request/response model is better.

Legacy names (`Register.Plugin`, `Register.CustomApi`, `ConfigureCustomApi`) are kept for backward compatibility, but new code should use the direct API (`RegisterSteps()` for plugins, `RegisterCustomApi(...)` for Custom APIs).

## Dependency injection (lightweight DI)

Override `ConfigureServices` in your plugin:

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    services.AddTransient<IMyService>(_ => new MyService(context.UserService));
}
```

Pattern used here: a lightweight **Dependency Injection / Service Locator hybrid** (composition in `ConfigureServices`, resolution via `ctx.GetRequiredService<T>()` at usage sites).  
Value: smaller handlers, easier unit tests, cleaner separation domain/infrastructure, and consistent dependency wiring.

Built-in registrations: `ILocalPluginContext`, `IPluginExecutionContext`, `IOrganizationService`, `ITracingService`, `ILogger`, `IPluginCacheService`

You can resolve registered services from handlers with:

```csharp
IMyService service = ctx.GetRequiredService<IMyService>();
```

## Caching (TTL + refresh)

Use the built-in `IPluginCacheService` for cross-execution, process-level caching:

```csharp
using Vela.Caching;

IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();

// Read-through cache with TTL
string configJson = cache.GetOrCreate(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.UserService));

// Force refresh when needed
string refreshed = cache.Refresh(
    "tenant:feature-config",
    TimeSpan.FromMinutes(5),
    () => LoadConfigurationFromDataverse(ctx.UserService));
```

Supported operations:
- `GetOrCreate(...)`
- `Refresh(...)`
- `Invalidate(key)` and `Clear()`

The cache surface is synchronous only: the plugin pipeline runs synchronously, and the cache locks
with `Monitor` (the `lock` statement), the most clearly sandbox-supported synchronization primitive.

Recommended cache usage patterns:

1. **Read-through with TTL**: use `GetOrCreate(...)` for values read frequently and updated occasionally.
2. **Targeted refresh**: use `Refresh(key, ttl, factory)` when you know data changed now.
3. **Targeted invalidation**: use `Invalidate(key)` for one entry (safer than global clear).
4. **Global reset**: use `Clear()` only for maintenance/debug scenarios.

Key design tips:

- Build stable keys with tenant/org + business identifier.
- Keep TTL explicit per use case (short for volatile data, longer for reference data).
- Cache pure/read-only results; avoid caching mutable workflow state.

## Unit-test your plugins (`Vela.Testing`)

The `Vela.Testing` package builds a fake plugin execution context so you can unit-test plugins and
Custom APIs **without a Dataverse connection** and **without depending on any mocking library**. Its
fluent builder mirrors the registration DSL (`OnUpdate().Stage(...).Mode(...)`), and the primary entity
name is derived from `[EntityLogicalName]`:

```powershell
dotnet add package Vela.Testing
```

```csharp
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Testing;

// Build a context and run the plugin in one chain:
AccountPostUpdate plugin = PluginContext.For<Account>()
    .OnUpdate()
    .Stage(PluginStages.PostOperation)
    .Mode(ExecutionMode.Synchronous)
    .WithTarget(new Account { Name = "Zava" })
    .WithPreImage(new Account { Name = "Old name" })
    .Execute<AccountPostUpdate>();

// ...or build a LocalPluginContext for direct assertions:
LocalPluginContext ctx = PluginContext.For<Account>()
    .WithTarget(new Account { Name = "Zava" })
    .Build();
```

- **Custom APIs / message-only plugins:** `PluginContext.ForMessage("vela_DoThing").WithInputParameter("amount", 5)`.
- **Bulk (`CreateMultiple`/`UpdateMultiple`):** `.WithTargets(account1, account2)`.
- **Test doubles (no Moq):** `FakeOrganizationService` (delegate hooks `OnCreate`/`OnRetrieve`/…),
  `FakePluginExecutionContext` (implements the full `IPluginExecutionContext2..7` chain, so plugins that
  read newer context members work), `RecordingTracingService` (assert via `builder.TraceCapture`).
- **Stateful in-memory CRM:** `InMemoryOrganizationService` for real Create/Retrieve/Update/Delete
  round-trips, bulk `CreateMultiple`/`UpdateMultiple` via `Execute(...)`, and a near-complete
  `RetrieveMultiple`: filters (many operators incl. Between, dates, EqualUserId, CompareColumns),
  conditions on linked entities, link-entities (Inner/LeftOuter/Exists/In/…, aliased columns, criteria,
  nested), Any/All related-record filters, ordering (incl. linked columns), TopCount, paging, Distinct,
  total record count, column aliases, aggregates, and **FetchXML** (common subset).
  Opt-in platform behaviours mirror Dataverse:

  ```csharp
  var crm = new InMemoryOrganizationService()
      .WithAlternateKey("account", "accountnumber")        // duplicate key -> throws
      .WithAutoNumber("task", "ticketnumber", "T-", 3);     // auto-populated on create: T-001, T-002, …

  // The trigger (Create/PostOperation) is auto-derived from the plugin's own registration —
  // no need to restate OnCreate().Stage(...).Mode(...).
  PluginContext.For<Account>()
      .WithPrimaryEntity("account", accountId)
      .WithTarget(new Account { Id = accountId, Name = "Zava" })
      .WithOrganizationService(crm)
      .Execute(new ProvisionAccountPlugin());

  Assert.Single(crm.GetAll("task")); // assert on the resulting CRM state
  ```
- **Standard system columns:** Create/Update auto-populate `createdon`/`createdby`/`modifiedon`/`modifiedby`,
  `statecode`/`statuscode` (active by default) and `versionnumber`, for user/team-owned tables `ownerid` +
  `owninguser`/`owningteam` + `owningbusinessunit`, and for org-owned tables `organizationid` (set the actor
  with `WithCurrentUser(...)` / `WithCurrentBusinessUnit(...)` / `WithOrganizationId(...)`, pin timestamps with
  `WithClock(...)`, and make ids reproducible with `WithIdSequence()`). Ownership type is inferred from the
  early-bound model and overridable per table with `AsOrganizationOwned(...)` / `AsUserOwned(...)` /
  `AsVirtual(...)` / `AsElastic(...)`. `Execute(new AssignRequest { ... })` reassigns the owner. See
  [`docs/Vela.Testing.md`](docs/Vela.Testing.md).
- **Bring your own service:** `.WithOrganizationService(myService)` accepts any `IOrganizationService`
  implementation when you need behaviour the built-in fakes don't cover.

### Simulate the pipeline (cascade)

For scenario tests that go beyond a single plugin, `PluginPipeline` simulates the Dataverse pipeline
**in memory**: writes through its service auto-fire the registered plugins (pre/post stages, execution
order, filtering attributes, pre/post images), and a plugin that writes triggers further plugins
(cascade). Registrations are **discovered automatically** from your assembly via `GetStepDefinitions()`.

```csharp
var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>()
    .WithAutoNumber("task", "ticketnumber", "T-", 3);

pipeline.Service.Create(new Account { Name = "Zava" });   // fires Create-account plugins, then cascades

Assert.Single(pipeline.GetAll("task"));        // assert on the resulting CRM state
Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountPostCreate")); // and on what ran
```

- Synchronous-plugin exceptions **roll the operation back** (snapshot/restore); **asynchronous** steps
  run inline after the synchronous commit. Cascade depth is guarded (the plugin's own depth guard plus a
  pipeline cap via `WithMaxDepth`).
- **Custom APIs** run through `Service.Execute(new OrganizationRequest("vela_MyApi"){ ["x"] = 1 })`.
- **Shared variables / `tag`.** A client passes the `tag` via a named request (not the shortcut methods, per
  the platform); the pipeline routes `Execute(CreateRequest/UpdateRequest/…)` through the registered steps and
  exposes it as a shared variable. Read it with `ctx.Tag` (or `ctx.GetSharedVariable("key")`):

  ```csharp
  // plugin: read the client tag
  protected override void Execute(ILocalPluginContext ctx, Account target)
  {
      if (ctx.Tag == "bulk-import") { /* skip enrichment for imports */ }
  }

  // test: pass it on the request
  var request = new CreateRequest { Target = new Account { Name = "Zava" } };
  request["tag"] = "bulk-import";
  pipeline.Service.Execute(request);
  ```

  For a single-plugin unit test, set it on the context builder instead: `PluginContext.For<Account>().….WithTag("bulk-import")`.
- Use `PluginPipeline.Empty().Register<MyPlugin>()` to scope a test to specific plugins.
- **Associate/Disassociate** fire their registered plugins, and the N:N intersect rows persist when the
  relationship is declared with `WithManyToMany(...)` (or derived from registered metadata) — the store has no
  live relationship metadata otherwise.

> **Full guide with examples:** [`docs/Vela.Testing.md`](docs/Vela.Testing.md).

### Security model (roles, privileges, business units, sharing, cascades)

By default the in-memory service grants **full access** — most plugin tests don't need security. Opt in with
`WithSecurity(...)` to exercise plugins that branch on access rights (privilege-denied paths,
`RetrievePrincipalAccess`, sharing). You declare business units, named roles, users and teams; the service then
enforces the table privileges (Create/Read/Write/Delete/Assign/Append/AppendTo/Share) at
**User / BusinessUnit / ParentChild / Organization** depth, over the user and every team they belong to. A denied
write throws `FaultException<OrganizationServiceFault>` with the `PrincipalPrivilegeDenied` code; `RetrieveMultiple`
silently filters to the records the user may read.

```csharp
using Vela.Testing.Security;

var crm = new InMemoryOrganizationService()
    .WithCurrentUser(aliceId)
    .WithSecurity(s => s
        .BusinessUnit(salesBu)
        .Role("salesperson", r => r
            .Grant("account", AccessLevel.BusinessUnit, Privilege.Create, Privilege.Read, Privilege.Write)
            .Grant("contact", Privilege.Read, AccessLevel.Organization))
        .User(aliceId, salesBu, "salesperson")
        .Team(salesTeam, salesBu, "salesperson")   // roles work on teams too
        .Member(aliceId, salesTeam))                // ...and members inherit the team's roles
    .Seed(/* records */);

// Alice reads/writes accounts in her business unit; records she cannot see are filtered out of
// RetrieveMultiple, and a write she lacks the privilege for throws PrincipalPrivilegeDenied.
```

- **Sharing (PoA).** `GrantAccess`/`ModifyAccess`/`RevokeAccess` share a specific record with a user or team —
  unioned with role access (a share only ever *adds* access, and can't exceed the sharer's own rights).
  `RetrievePrincipalAccess` returns the effective rights, `RetrieveSharedPrincipalsAndAccess` lists a record's
  shares, and `WithSharedRecord(record, principal, AccessRights.ReadAccess)` pre-shares in arrange.
- **Ownership & assignment.** Owning a record (directly or via a team) satisfies the User depth; changing `ownerid`
  (through `AssignRequest` or a plain Update) needs the **Assign** privilege, and the owning business unit follows
  the new owner.
- **Relationship cascades.** Declare 1:N relationships with `WithParentalRelationship(...)` /
  `WithReferentialRelationship(..., restrictDelete: true)`. Deleting a parent then cascades per the relationship —
  **Cascade** deletes the children, **RemoveLink** clears their lookup, **Restrict** blocks the delete while
  children exist. Assign and Share cascade the same way, and in a parental relationship a child **inherits** the
  caller's access to its parent (re-derived automatically when the child is re-parented).
- **Access teams.** `WithAccessTeamTemplate(templateId, rights)` registers a template's rights; a plugin then
  grants a user those rights on a record with `AddUserToRecordTeamRequest` (and removes them with
  `RemoveUserFromRecordTeamRequest`) — unioned with role and shared access.
- **Hierarchy security.** `ManagerHierarchy(depth)` + `Manager(userId, managerId)` let a manager reach a report's
  records up the reporting chain: read/write on a direct report, read-only deeper (within depth), provided they
  hold BusinessUnit/Deep role access and are in the report's business unit or its parent.
- **Field-level security.** `SecureColumn(entity, column)` marks a column secured; `FieldPermission(principal,
  entity, column, read:, update:)` grants it. A secured column is hidden on read and rejected on write without
  permission — independent of record access.
- **Role membership.** With security configured, the platform's membership tables (`role`, `systemuserroles`,
  `teamroles`, `teammembership`) are queryable, so a plugin can resolve a user's roles as it does against
  Dataverse. Generate a `Roles` class with the **real** role privileges + ids from your environment with
  [`vela models --roles`](#early-bound-generation-vela-models), then `Role(Roles.XName, Roles.XId, Roles.X)`.
- **Off by default** — any test that never calls `WithSecurity(...)` behaves exactly as before (full access).

## End-to-end example

A complete loop from plugin code to a registered, running step, using a solution scaffolded by
[`vela init`](#scaffold-a-new-solution-vela-init).

**1. Write a plugin** in your plugin-package project (`Zava.Plugins`, which references `Vela.Plugins`):

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.StepBuilder;
using Zava.Shared.Models;

public class AccountPostUpdate : EntityPluginBase<Account>
{
    public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

    protected override void RegisterPluginSteps()
    {
        RegisterSteps().OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)   // only fire when Name changes
            .PreImage()
                .Attributes(a => a.Name);
    }

    protected override void Execute(ILocalPluginContext ctx, Account target)
    {
        var before = ctx.GetPreImage<Account>();
        ctx.Trace($"Account renamed from '{before.Name}' to '{target.Name}'");
    }
}
```

**2. Build the plugin package** into a `.nupkg`:

```powershell
dotnet pack .\src\Zava.Plugins -c Release -o .\artifacts
```

**3. Preview** what would change in the target environment (read-only):

```powershell
vela plan --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive --verbose
```

`plan` prints a preview — e.g. `summary: +1 -0 ~0` plus an ordered `operations` list — and makes no changes.

**4. Apply** the changes (idempotent create/update/delete):

```powershell
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive --execute --verbose
```

`apply --execute` uploads the plugin package, ensures the plugin type exists, then registers the step and its image. Re-running with no local changes is a no-op. If a run fails partway, the resume report on stderr lists which operations completed and which did not — fix the cause and re-run (upserts are idempotent).

**5. Verify** in Dataverse: update an account's name and confirm the step fires (its message appears in the plugin trace log).

## Cookbook (common recipes)

1. **Single-step update plugin**
   - `RegisterPluginSteps()` + `RegisterSteps().OnUpdate().Stage(...).Mode(...).FilteringAttributes(...)`
2. **Multi-step router plugin**
   - `RegisterPluginSteps()` + repeated `RegisterSteps().On...().Handler(...)`
3. **Bound Custom API**
   - `RegisterCustomApi("...", Handler).AsAction().BoundTo<TEntity>()`
4. **Unbound typed Custom API**
   - `RegisterCustomApi("...").Request(...).Response(...)` + typed `Execute(...)`
5. **DI in handlers**
   - register in `ConfigureServices(...)`, consume via `ctx.GetRequiredService<T>()`, cache shared data with `IPluginCacheService`

## Troubleshooting

1. **`RegisterCustomApi(...) can only be called inside RegisterCustomApi()`**
   - Call it only from the override `RegisterCustomApi()` method.
2. **Step handler not found / wrong signature**
   - Ensure instance method signature is `void Handler(ILocalPluginContext, TEntity)`.
3. **Custom API handler not found / wrong signature**
   - Ensure signature is `void Handler(ILocalPluginContext)` for lightweight mode.
4. **No changes in `plan`**
   - Confirm the `.nupkg` path, version, and environment connection are correct.
5. **Unexpected legacy warnings**
   - Migrate to direct patterns (`RegisterPluginSteps()` + `RegisterSteps().On...()`, then in custom APIs override `RegisterCustomApi()` and call `RegisterCustomApi(...)`).
6. **`The plug-in type could not be found in the plug-in assembly` at runtime**
   - Dataverse caches plugin assemblies **by version**, so re-deploying changed code under the **same** assembly version can keep serving the old assembly. **Bump the assembly version** and rebuild. Projects scaffolded by `vela init` auto-increment `AssemblyVersion` (`1.0.*`) to avoid this; if you add a new plugin type without bumping the version, `vela apply` **fails fast** with this guidance rather than letting it break at runtime.

## Sign the plugin package (`vela sign`)

When a plugin uses a **managed identity**, Dataverse requires the plugin **package** (`.nupkg`) to carry a
trusted signature. `vela sign` applies one by calling `dotnet nuget sign` (so it runs anywhere the .NET SDK
does — Windows, Linux, macOS):

```powershell
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --fingerprint <sha256-fingerprint>
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --pfx cert.pfx --pfx-credential <cred>
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --pfx-base64 $env:PFX_B64 --pfx-password $env:PFX_PW
```

Certificate sources (exactly one): `--fingerprint` (cert in `CurrentUser\My` — recommended, no secret on
disk), `--pfx <file>` with `--pfx-password` or `--pfx-credential` (a Windows Credential Manager entry), or
`--pfx-base64 <content>` (a CI secret, decoded to a temp file and deleted after). Optional `--timestamp`
(default `http://timestamp.digicert.com`).

`vela init --managed-identity` wires this into the build of the **Integrations** project: a `SignPluginPackage`
MSBuild target runs after `Pack`, **inert until a certificate source is configured** and **skipped inside CI**
(`GITHUB_ACTIONS` / `TF_BUILD`) so a pipeline owns its own sign step — no double-signing. Configure it with
MSBuild properties, e.g. `dotnet pack src/Zava.Integrations -c Release -o artifacts
-p:PluginSigningFingerprint=<sha256>` (or `-p:PluginSigningCertificate=path.pfx -p:PluginSigningCredential=<cred>`).
The generated solution's README documents the full property set.

## CLI automation

```powershell
# 1) Compare local package(s) with live Dataverse state (client id / client secret)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 2) Multiple packages
vela plan `
  --packages-dir .\artifacts `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 3) Preview apply operations (dry-run) against the same environment
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 3b) Execute apply operations (idempotent upsert/delete)
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --execute

# 4) Interactive local mode (wizard prompts)
vela plan --interactive

# 5) CI strict mode (fail if scanner/metadata diagnostics exist)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --strict
```

`plan` compares local plugin/custom-api metadata extracted from `.nupkg` against Dataverse and returns a preview: `hasChanges`, `summary`, and ordered `operations` (`create`, `update`, `delete`) plus detailed `diff`.

`apply --execute` applies Dataverse create/update/delete operations to converge with local package metadata, then prints the post-apply state diff.

## Registration manifest generation

Use `FrameworkManifestScanner` to extract both plugin steps and Custom APIs:

```csharp
var manifest = FrameworkManifestScanner.Scan(typeof(AccountPostUpdate).Assembly);
foreach (var def in manifest.PluginSteps)
{
    Console.WriteLine($"{def.PluginTypeName}: {def.Message} / {def.Stage}");
    Console.WriteLine($"  Filtering: {string.Join(", ", def.FilteringAttributes)}");
    Console.WriteLine($"  PreImages: {string.Join(", ", def.PreImages.Select(i => i.Alias))}");
}
```

Use strict mode in CI when you want scanner diagnostics to fail the build:

```csharp
var manifest = FrameworkManifestScanner.Scan(
    typeof(AccountPostUpdate).Assembly,
    new FrameworkManifestScanOptions { Strict = true });
```

Typed custom SDK messages can use `OnMessage<TMessage>()` with `[CustomMessage("...")]` to keep intellisense without string literals.

## Early-bound generation (`vela models`)

Generate early-bound model classes over the **same Dataverse connection** as `plan`/`apply` (no separate
`pac auth`). Scope by **solution** (which `pac modelbuilder` can't) and/or an explicit table list:

```powershell
# from the solution's components, into <prefix>.Shared.Models.Generated (inferred from the project)
vela models --solution ZavaSales --url https://zava.crm.dynamics.com --interactive

# or an explicit list, with an explicit namespace/output
vela models --entities account,contact --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated --overwrite

# typed Request/Response message classes for Custom APIs
vela models --custom-apis zava_Recalculate --interactive

# security roles → a Roles class for the test project (real privileges + root role id)
vela models --roles "Salesperson,Sales Manager" --namespace Zava.Tests.Roles --out tests/Zava.Tests/Roles
vela models --solution ZavaSales --solution-roles --namespace Zava.Tests.Roles --out tests/Zava.Tests/Roles
```

Generated classes are `partial` (extend them in a sibling file), keep the primary id synced with `Entity.Id`,
carry XML doc (display name / description / constraints / lookup targets), DataAnnotations
(`[StringLength]`/`[Range]`, omit with `--no-annotations`) and two self-contained facet attributes that no
standard annotation can express — `[RequiredLevel(...)]` (the full four-value Dataverse required level, not a
lossy boolean) and `[LookupTargets(...)]` (a lookup / Customer / polymorphic column's target tables). The test
harness's `FakeEntityMetadata.From<T>()` recovers all of these by reflection, so a derived fake metadata carries
required level and lookup targets with no hand-written overrides. `vela models` turns choices into enums (local choices nested in
the table, global option sets as shared enums; multi-select choices become an `IList<Enum>`), type **File**
columns as `Guid?` (the `fileid`) and **Image** columns as `byte[]` (the thumbnail), and expose alternate keys as typed factories
(`Account.ByAccountNumberKey("A-001")` → a key-based `EntityReference`). `--custom-apis` instead generates typed
`OrganizationRequest`/`OrganizationResponse` message classes (call with `service.Execute(new zava_RecalculateRequest { … })`).
`--roles` (explicit names) / `--solution-roles` (every role in a solution) instead generate a `Roles` class **for the
test project** — each role's `Name`, root `Id` and a configurator carrying its real privileges — so tests drive the
[security model](#security-model-roles-privileges-business-units-sharing-cascades) with the actual role configuration.
Connection flags are the same as `plan`/`apply`
(`--connection-string`, or `--url` + `--tenant-id` + `--client-id` + `--client-secret`, or `--interactive`).

> Out of scope for now (use `pac modelbuilder` if you need them): relationship navigation properties and an
> `OrganizationServiceContext`/LINQ provider. See [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md).

## Limitations & not supported

Vela makes deliberate trade-offs (plugin-sandbox safety, a preview-first `plan`/`apply` scope, and a
faithful-but-partial test double). What's intentionally out of scope — across `Vela.Plugins`, `Vela.Cli` and
`Vela.Testing` — is collected in [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md).

## Contributing

Want to build or contribute to the Vela framework itself (not just use it)? See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the repository layout, architecture rules, and how to build and
test from source.

## License

Released under the [MIT License](LICENSE).

## Acknowledgments

Built on the Microsoft Dataverse SDK (`Microsoft.Xrm.Sdk`, `Microsoft.PowerPlatform.Dataverse.Client`).
