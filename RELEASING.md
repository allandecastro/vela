# Releasing

Vela ships as two independent release lines, each cut by pushing a git tag. The
[`release.yml`](.github/workflows/release.yml) workflow does the rest: build, test, pack, publish to
NuGet.org via Trusted Publishing (OIDC, no stored API key), and create a GitHub Release.

| Line | Tag | Packages |
| --- | --- | --- |
| Framework | `framework-vX.Y.Z` | `Vela.Plugins` + `Vela.Testing` (versioned together) |
| CLI | `cli-vX.Y.Z` | `Vela.Cli` |

The tag is the single source of truth for the version; the `<Version>` in each `.csproj` is only the
local-dev default.

## One-time setup (NuGet.org)

1. Create a **Trusted Publishing** policy on nuget.org for the package owner `AllanDeCastro`, bound to
   repository `allandecastro/vela` and workflow `release.yml`, with the **Push new packages and package
   versions** scope and glob `Vela.*`. Leave Environment empty.
2. Optionally reserve the `Vela.*` ID prefix.
3. A fresh policy must be used within 7 days to stay active; the first successful publish activates it
   permanently.

## Cut a release

1. Move the relevant entries from `## [Unreleased]` to a new `## [X.Y.Z] - YYYY-MM-DD` section in
   [`CHANGELOG.md`](CHANGELOG.md) (open a PR, merge to `main`).
2. Make sure the self-hosted Windows runner is online (the net462 harness only packs on Windows).
3. Tag and push:
   ```bash
   git tag framework-v0.1.0 && git push origin framework-v0.1.0   # Vela.Plugins + Vela.Testing
   git tag cli-v0.1.0       && git push origin cli-v0.1.0         # Vela.Cli
   ```
4. Watch the run under the repo's Actions tab. On success the packages are live on NuGet.org and a GitHub
   Release is created. `--skip-duplicate` keeps re-running the same tag safe.

## Versioning

Pre-1.0 (`0.y.z`): a minor bump may break the API; a patch bump is backwards-compatible fixes only. From 1.0
onward, standard SemVer. See the versioning policy at the top of [`CHANGELOG.md`](CHANGELOG.md).
