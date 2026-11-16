# Releasing

Vela ships as two independent release lines, each cut by pushing a git tag. The
[`release.yml`](.github/workflows/release.yml) workflow does the rest: build, test, pack, publish to
NuGet.org via Trusted Publishing (OIDC, no stored API key), and create a GitHub Release. It runs on a
GitHub-hosted `windows-latest` runner (the net462 harness only builds on Windows), like `ci.yml`.

| Line | Tag | Packages |
| --- | --- | --- |
| Framework | `framework-vX.Y.Z` | `Vela.Messages` + `Vela.Plugins` + `Vela.Testing` (versioned together; `Vela.Testing` depends on the exact `Vela.Plugins` version) |
| CLI | `cli-vX.Y.Z` | `Vela.Cli` |

The tag is the single source of truth for the version; the `<Version>` in each `.csproj` is only the
local-dev default, `0.0.0-local`, which never collides with a published version.

## One-time setup (NuGet.org)

1. Create a **Trusted Publishing** policy on nuget.org for the package owner `AllanDeCastro`, bound to
   repository `allandecastro/vela` and workflow `release.yml`, with the **Push new packages and package
   versions** scope and glob `Vela.*`. Leave Environment empty.
2. Optionally reserve the `Vela.*` ID prefix.
3. A fresh policy must be used within 7 days to stay active; the first successful publish activates it
   permanently.

## Cut a release

1. In [`CHANGELOG.md`](CHANGELOG.md) (open a PR, merge to `main`), cut the line's section out of `## [Unreleased]`.
   `Unreleased` holds both lines, each heading qualified by its line; a release takes only its own line's entries:
   - Add a section right below `## [Unreleased]`, headed `## [X.Y.Z] - YYYY-MM-DD (Vela.Cli)` for the CLI, or
     `## [X.Y.Z] - YYYY-MM-DD (Vela.Messages + Vela.Plugins + Vela.Testing)` for the framework.
   - Move into it every entry under a heading of that line, and drop the line from the heading:
     `### Changed (Framework, breaking)` becomes `### Changed (breaking)`, `### Added (CLI)` becomes `### Added`,
     `### Fixed (Framework)` becomes `### Fixed`, and so on. Keep the order `Changed (breaking)`, `Added`,
     `Changed`, `Fixed`. Remove the headings left empty in `Unreleased`.
   - `### Docs` entries have no line: move them with the release they describe, or leave them for the next one.
   - Entries of the other line stay under `## [Unreleased]`, under their qualified headings.
   - A CLI release that follows a framework release bumps `ScaffoldOptions.DefaultCoreVersion` to that framework
     version (a test fails until it matches the newest framework section) and says so under `### Changed`.
2. For a framework release, in the same PR: move the rules listed in
   [`src/Vela.Analyzers/AnalyzerReleases.Unshipped.md`](src/Vela.Analyzers/AnalyzerReleases.Unshipped.md) (its
   `### New Rules`, `### Removed Rules` and `### Changed Rules` tables) to a new `## Release X.Y.Z` section at the
   end of [`AnalyzerReleases.Shipped.md`](src/Vela.Analyzers/AnalyzerReleases.Shipped.md), in the same table
   format, and leave only the header comment in `AnalyzerReleases.Unshipped.md`.
3. Tag and push:
   ```bash
   git tag framework-v0.1.0 && git push origin framework-v0.1.0   # Vela.Messages + Vela.Plugins + Vela.Testing
   git tag cli-v0.1.0       && git push origin cli-v0.1.0         # Vela.Cli
   ```
4. Watch the run under the repo's Actions tab. On success the packages are live on NuGet.org and a GitHub
   Release is created. `--skip-duplicate` keeps re-running the same tag safe.

## Framework version in scaffolded projects

`vela init` resolves, from the nuget.org registration index, the newest **listed** stable `Vela.Plugins` version
on the release line of `ScaffoldOptions.DefaultCoreVersion`: the same minor before 1.0, the same major from 1.0. A
framework patch release is picked up with no CLI release; an unlisted version is never picked. A new framework
minor (or, from 1.0, major) may break the code the scaffold generates, so `vela init` uses it only once a CLI release
bumps `DefaultCoreVersion` to it. When nuget.org is unreachable or lists no compatible version, `vela init` uses
`DefaultCoreVersion` itself. `--core-version X.Y.Z` pins a specific version.

## Versioning

Pre-1.0 (`0.y.z`): a minor bump may break the API; a patch bump is backwards-compatible fixes only. From 1.0
onward, standard SemVer. See the versioning policy at the top of [`CHANGELOG.md`](CHANGELOG.md).
