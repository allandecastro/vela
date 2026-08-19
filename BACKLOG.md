# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list — keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## Testing & CI

- **P2 — Lift the `// Stryker disable all` on `PackageScanner.BuildLocalStateFromPackages`.**
  `PackageScannerBuildStateTests` (with the dependency-free `SamplePluginFixture` assembly) now drives it
  end-to-end on a real compiled `.nupkg` (extract → `Assembly.LoadFrom` → reflect both plugin kinds → normalize
  → assemble state). To drop the annotation, also cover the `BuildIntrospectionFailure` branch (a second
  broken-plugin fixture) and confirm the mutation score on the Windows runner.
- **P2 — Real-Dataverse integration test tier.** Beyond the generate-then-build smoke test, run `plan`/`apply`
  against a live dev org to catch CLI regressions the in-memory / offline tests can't.

## Release

- **P3 — nuget.org publishing path.** The packages are not yet published; document and automate the release.
