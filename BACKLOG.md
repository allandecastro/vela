# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

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
