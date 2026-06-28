# Rename plan — `Dataverse.Framework.*` → `Vela.*`

Mechanical but wide-reaching: **113 `.cs` files** (~394 occurrences), 4 `.csproj`, the `.sln`,
2 CI workflows, and docs. This plan keeps it safe and reviewable.

Brand-first naming: we drop **both** "Dataverse" and "Framework" from the IDs — the brand is
`Vela`, and "Dataverse" lives in the package Title/Description/Tags for discoverability.

> **Clean break.** Nothing is published / no public consumers yet, so we do a hard switch with
> **no back-compat shims**: no `dvf` alias, no deprecation notes. Old names are simply gone.

> **GitHub repo/org** is renamed **later**. Until then, `PackageProjectUrl` / `RepositoryUrl`
> use a placeholder (`https://github.com/<org>/vela`) — fill in when the repo move happens.

## 1. Target naming

| Today | After |
| --- | --- |
| Package `Dataverse.Framework.Core` | `Vela.Core` |
| Package / tool `Dataverse.Framework.Tools` | `Vela.Cli` |
| CLI command `dvf` | `vela` |
| Namespace root `Dataverse.Framework.Core.*` | `Vela.Core.*` |
| Namespace root `Dataverse.Framework.Tools.*` | `Vela.Cli.*` |
| Assembly names (same as packages) | `Vela.Core` / `Vela.Cli` |
| Solution `Dataverse.Framework.sln` | `Vela.sln` |
| Test projects `Dataverse.Framework.Tests` / `.Tools.Tests` | `Vela.Tests` / `Vela.Cli.Tests` |

> Namespaces just swap the `Dataverse.Framework` prefix for `Vela`; the sub-segments
> (`.Plugin`, `.StepBuilder`, `.Context`, `.Caching`, …) stay identical, so the consumer's mental
> model is unchanged — only the prefix moves (`Dataverse.Framework.Core.Plugin` → `Vela.Core.Plugin`).

## 1b. Discoverability (since "Dataverse" leaves the ID)

Add to both `.csproj` so nuget.org search still surfaces the packages:

```xml
<Title>Vela — Build Dataverse with confidence</Title>
<Description>Modern Microsoft Dataverse development: plugins, Custom APIs, with a safe
  preview-first plan/apply workflow.</Description>
<PackageTags>dataverse;dynamics365;power-platform;plugin;custom-api;xrm;cli</PackageTags>
```

## 2. Order of operations (one PR, reviewable commits)

1. **csproj / sln / folders** — rename project folders + files, update `<PackageId>`,
   `<AssemblyName>`, `<RootNamespace>`, `<ToolCommandName>vela`, and `ProjectReference` paths.
   Rename `Dataverse.Framework.sln` → `Vela.sln`.
2. **Namespaces & usings in code** — replace the `Dataverse.Framework` prefix with `Vela`
   across all `.cs` (namespace declarations + `using` statements). See the ordered `sed` in §6.
3. **Generated templates** (`Cli/ScaffoldTemplates.cs`) — the scaffolder emits consumer files that
   reference `Dataverse.Framework.Core` and the `dvf` command. Update those template strings too,
   **plus** `Cli/ToolInfo.cs` (`ToolName = "dvf"` → `"vela"`) and `Cli/InteractiveMenu.cs` titles.
4. **CI** — `.github/workflows/ci.yml`, `release.yml`: solution name, package paths, tool install,
   tag prefixes (see versioning).
5. **Docs** — `README.md` (install command, `dvf` → `vela`, package names) and
   `branding/DESIGN_SYSTEM.md`. Also **reword the "Terraform-style / Terraform-like" wording**
   in the README to neutral phrasing (e.g. "a preview-first plan/apply workflow") — we don't
   position Vela against Terraform.
6. **Branding & NuGet packaging** (see §4 — this is the part beyond a pure rename).
7. **Build + test** — `dotnet build Vela.sln -c Release` then `dotnet test`. Run the opt-in smoke
   test (`DVF_BUILD_SMOKE=1`) since it packs Core and scaffolds a real consumer solution.

## 3. Watch-outs

- **Core↔CLI contract test** — must stay green; rename both sides in the same commit.
- **Build smoke test** (`Generate_ThenDotnetBuild_Succeeds`) — packs Core + scaffolds a project that
  references the package by name; templates and the packed PackageId must agree.
- **`nuget.config` template** — generated solutions point a `local-packages/` feed at `Dataverse.Framework.Core`;
  update to `Vela.Core`.
- **`dvf` → `vela`** is a hard switch (no alias). Fine — no installs exist yet.

## 4. Branding & NuGet packaging

`Core.csproj` today has **no packaging metadata at all** (no `PackageId`, `Version`,
`Description`, icon, readme). Both packages need proper metadata + the Vela icon before
they look right on nuget.org.

**Shared metadata → `Directory.Build.props`** (guarded so test projects don't pack):

```xml
<PropertyGroup Condition="'$(IsPackable)' == 'true'">
  <Authors>allan.de.castro</Authors>
  <Company>Vela</Company>
  <PackageProjectUrl>https://github.com/<org>/vela</PackageProjectUrl>
  <RepositoryUrl>https://github.com/<org>/vela</RepositoryUrl>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PackageIcon>icon.png</PackageIcon>
  <PackageTags>dataverse;dynamics365;power-platform;plugin;custom-api;xrm;cli</PackageTags>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
</PropertyGroup>
<ItemGroup Condition="'$(IsPackable)' == 'true'">
  <None Include="$(MSBuildThisFileDirectory)branding/png/icon.png" Pack="true" PackagePath="\" />
  <None Include="$(MSBuildThisFileDirectory)README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

**Per-package** (`Vela.Core` / `Vela.Cli` csproj): `PackageId`, `Title`, `Description`, `Version`.

**Icon asset:** NuGet's `PackageIcon` must be a **raster** (PNG/JPG, SVG not supported), max
1 MB, recommended **128×128**. Use the tile → copy `branding/png/vela-tile.svg.png` to
`branding/png/icon.png` (already a clean 128-based render). The favicon/SVGs stay for web use.

**README:** the same `README.md` is embedded in the package; add the Vela icon at the top:

```markdown
<img src="branding/png/vela-tile.svg.png" width="72" align="left" />

# Vela
**Build Dataverse with confidence** — modern Microsoft Dataverse development.
```

> `Core` packs for `net462` — confirm the icon/readme `None` includes still apply (they do;
> they're framework-agnostic pack items).

## 5. Versioning (independent per package, same repo)

- Do **not** add a global `<Version>` to `Directory.Build.props`.
- Fresh package IDs, no prior releases → start **both** `Vela.Core` and `Vela.Cli` at `1.0.0`
  (or `0.1.0` if you prefer signalling pre-stable).
- Tag releases by prefix: `core-vX.Y.Z`, `cli-vX.Y.Z`; CI publishes only the matching package.

## 6. Mechanical command (review the diff before committing)

```bash
# namespaces + usings in source (after folders/csproj are renamed)
# Order matters: map the CLI (.Tools → .Cli) and Core first, then the rest (tests).
grep -rIl 'Dataverse.Framework' --include='*.cs' . | xargs sed -i '' \
  -e 's/Dataverse\.Framework\.Tools/Vela.Cli/g' \
  -e 's/Dataverse\.Framework\.Core/Vela.Core/g' \
  -e 's/Dataverse\.Framework/Vela/g'
```

> Result: `Dataverse.Framework.Core` → `Vela.Core`, `Dataverse.Framework.Tools` → `Vela.Cli`,
> `Dataverse.Framework.Tools.Tests` → `Vela.Cli.Tests`, `Dataverse.Framework.Tests` → `Vela.Tests`.
> We drop **both** "Dataverse" and "Framework". Then verify there are no remaining
> `Dataverse.Framework` hits and no `Vela.Vela` double-prefix afterwards.
