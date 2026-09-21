# Analyzers and generators

Vela ships a small set of Roslyn **analyzers** and one **source generator** inside the `Vela.Plugins` package
(they live in the bundled `Vela.Analyzers` assembly). There is **no separate analyzer package or project to
reference** - they come with `Vela.Plugins` automatically and run in the editor and at build with no extra
configuration. Each analyzer is reported as an **error** so a mistake fails fast instead of surfacing as a silent
runtime surprise. Together they let the compiler enforce the fluent step builder's rules, so both a human
developer and an AI author get told what is valid up front.

- [VELA001 - column selector targets a real column](#vela001)
- [VELA002 - step chain must end with Handle](#vela002)
- [VELA003 - a step modifier is set at most once](#vela003)
- [Typed image projection generator](#typed-image-projection-generator)

## VELA001

**A column selector must target a real column, not a base `Entity` member.** The column selectors used by
`FilterOn(...)`, the image selectors in `WithPreImage(...)` / `WithPostImage(...)`, and the typed query / change
helpers accept an early-bound column, e.g. `a => a.Name`. Selecting a member of the base `Microsoft.Xrm.Sdk.Entity`
type instead (such as `a => a.Id` or `a => a.LogicalName`) does not name a Dataverse column and is flagged.

**Fix:** select a generated column of your entity (`a => a.Name`), not an `Entity` member.

## VELA002

**A `RegisterStep()` chain must end with `.Handle(...)`.** The redesigned builder puts `Handle` last (it returns
`void`), so a step that stops earlier never binds a handler and would have nothing to run. Any discarded
`RegisterStep()....;` statement whose value is still a fluent builder is flagged.

**Fix:** end the chain with `.Handle(handler)`. (Chains captured in a local to be completed later are not flagged.)

## VELA003

**Set each single-value modifier at most once per step.** `FilterOn(...)` and the `With...` setters
(`WithExecutionOrder`, `WithDescription`, `WithStepKey`, `WithImpersonation`, `WithSupportedDeployment`,
`WithUnsecureConfig`, `WithSecureConfig`) overwrite the earlier value when called twice, silently dropping it, so a
second call is flagged. `Disabled()` is exempt (it is an idempotent flag toggle). The runtime builder throws on the
second call as well, so a chain split across locals is still caught at registration.

**Fix:** call the modifier once (combine the arguments if needed, for example a single `FilterOn(a => a.A, a => a.B)`).

## Typed image projection generator

The same bundled `Vela.Analyzers` assembly also contains an incremental **source generator**
(`ImageProjectionGenerator`) - so, like the analyzers, it ships with `Vela.Plugins` and needs no separate package
or project. For every step that captures explicit columns in `WithPreImage(a => a.X, ...)` /
`WithPostImage(...)`, it emits a per-step projection type holding **only** those columns, which you read with
`ctx.PreImageAs<T>()` / `ctx.PostImageAs<T>()`. Reading a column the step did not capture is then a compile
error instead of a silent `null`. See [Typed images](../guides/plugins.md#typed-images-compile-safe-projections)
in the plugin guide for the full usage.

Key points:

- **Compile-time, regenerated every build.** The projections are produced in memory from the capture lambdas
  against your current early-bound model (they are not files in your source tree; `F12` opens them). If a
  captured column is removed or renamed in the early-bound model (for example after re-running `vela models`),
  the `WithPreImage`/`WithPostImage` selector and the handler that reads the projection stop compiling - the
  drift surfaces as a build error, never a stale value.
- **Naming.** A projection is named `{PluginClass}_{Handler}_{Pre|Post}Image` (plus the alias when one is given),
  and a `global using` for `<YourNamespace>.Generated.Images` is emitted on C# 10+ so the type is offered by
  completion without an explicit `using`.
- **Opt-in and additive.** A capture with no explicit columns (`WithPreImage()`) keeps the full early-bound
  entity and generates nothing; if the column list is not a literal set of selectors, the generator skips it and
  you keep the untyped image.
