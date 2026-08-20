# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## Release

- **P3 - first nuget.org publish.** The `release.yml` workflow already packs and publishes on a
  `framework-v*` / `cli-v*` tag via Trusted Publishing (OIDC, no stored key). What remains is the nuget.org
  side: configure the Trusted Publishing policy for this repo, optionally reserve the `Vela.*` ID prefix, then
  push the first release tag. A short release runbook would help.
