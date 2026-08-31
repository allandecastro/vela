# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

- **P2 - reject unknown options on the remaining commands.** `plan` / `apply` / `inspect` now fail fast on an
  unknown/misspelled option (`TryValidateKnownOptions` in the shared parser). `init`, `add`, `models` and `sign`
  each have their own ad-hoc parser that still silently ignores unrecognized `--flags`, so a typo there is
  swallowed the same way. Extend the same allowlist guard to those commands (one known-option set per command,
  next to its parser), and add tests. Watch the positional-heavy commands (`add <kind> <name>`, `init <name>`)
  so real positionals are not misread as unknown options.
- **P1 - scaffold packages the plugin with `dotnet build`, not `dotnet pack`.** The `.csproj` uses
  `Microsoft.PowerApps.MSBuild.Plugin` with `GeneratePackageOnBuild=true`; that SDK bundles the dependent
  assemblies from the *publish* output, and publish only runs on **build** (`PublishOnBuild` +
  `_PowerAppsPlugin_PublishAfterBuild AfterTargets="Build"`). A cold `dotnet pack -c Release -o artifacts` (the
  currently documented step) does not publish first, so it emits an assembly-less package (`NU5128`, empty
  `lib/net462`) and `vela plan` then fails with "No assemblies found in package". Verified: clean
  `dotnet build -c Release` produces the correct 20-assembly package; clean `dotnet pack` produces 0. Fix: set
  `<PackageOutputPath>$(MSBuildProjectDirectory)\..\..\artifacts</PackageOutputPath>` on the Plugins/Integrations
  csproj (a plain `dotnet build -c Release` then drops the correct package into `artifacts/`), and replace every
  documented `dotnet pack ... -o artifacts` with `dotnet build -c Release` (README next-steps, scaffold/register
  docs, and the signing docs' `dotnet pack -p:...` -> `dotnet build -p:...`; the signing target is
  `AfterTargets="Pack"`, which still fires under `GeneratePackageOnBuild`). Update golden/scaffold tests + CHANGELOG.
- **P2 - persistent auth cache for interactive login.** `vela plan/apply/inspect --interactive` re-prompts the
  Dataverse URL and opens a browser login on every invocation: MSAL only caches the token in-memory (lost between
  CLI runs) and the URL is not remembered. Add a persisted MSAL token cache (e.g. `Microsoft.Identity.Client`
  token-cache serialization under the user profile) and remember the last URL, so a repeat run reuses the token
  silently. Mind secret hygiene (no tokens in world-readable files) and an opt-out/clear path.
- **P2 - Windows temp-dir cleanup after assembly introspection.** `PackageScanner` does
  `Assembly.LoadFrom(...)` to read step definitions; on Windows this locks the DLL for the process lifetime, so
  the best-effort temp cleanup fails with "Access to the path 'Vela.Plugins.dll' is denied" and leaks a
  `vela-tools\<guid>` directory each run. Load the assembly into a collectible `AssemblyLoadContext` and unload it
  before deleting (or load via bytes) so the files can be removed. Note: the scan still runs user code, so this is
  about cleanup/noise, not the security posture (see `PackageScanner` comment).
- **P3 - surface a stale-package hint.** When `vela plan` reads step registrations that do not match the source
  (e.g. a step edited to `OnDelete`/`PostOperation` but the `.nupkg` still carries the old `OnCreate`/`PreValidation`
  because it was not rebuilt), the error names the *old* message/stage and reads as a mystery. This is a natural
  consequence of a stale artifact (related to the `dotnet build` fix above), but the CLI could help: e.g. warn when
  a package file is older than its source assembly's project, or add a "the package may be out of date - rebuild"
  hint to the introspection-failure message.
- **P3 - ribbon deploy end to end.** The classic `RibbonDiffXmlGenerator` (+ `RibbonSchema` validation) exists as
  a utility, but nothing wires the command bar automatically: registering a command on a button is left to the
  developer (Power Apps command designer, or a hand-authored `RibbonDiffXml`). Automating it would need a source
  of truth for the exported command functions (a build manifest from `vela-client`, or a scan) plus a Dataverse
  apply (modern `appaction` records / solution import). Deferred: low value versus letting the dev own the
  wiring. Design: `vela-client` `budapest/docs/plans/ribbon-command-bar.md` (PR-R4).
- **P3 - scaffold resolves Vela from nuget.org.** Now that `Vela.Plugins` and `Vela.Testing` are published,
  `vela init` still emits a `nuget.config` with a `local-packages/` feed (a workaround from before the packages
  shipped). Update the scaffold templates so a generated project resolves the Vela packages from nuget.org
  directly, and drop or simplify the local feed. Refresh the scaffold docs (scaffold.md, initialize-a-solution.md)
  to match.
