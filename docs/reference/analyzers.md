# Analyzers (VELA001 - VELA004)

Vela ships a small set of Roslyn analyzers inside the `Vela.Plugins` package. They run in the editor and at
build with **no extra reference or configuration**, and each is reported as an **error** so a mistake fails fast
instead of surfacing as a silent runtime surprise. Together they let the compiler enforce the fluent step
builder's rules, so both a human developer and an AI author get told what is valid up front.

- [VELA001 - column selector targets a real column](#vela001)
- [VELA002 - step chain must end with Handle](#vela002)
- [VELA003 - a step modifier is set at most once](#vela003)
- [VELA004 - typed image projection reads only captured columns](#vela004)

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

## VELA004

**A typed image projection must read only columns the step captured.** For a typed-image handler
(`Handle<TImg>` / `Handle<TPre, TPost>` / `HandleWithPreImage` / `HandleWithPostImage`), each projection's columns
must be a subset of the columns the step declared in the matching `WithPreImage(...)` / `WithPostImage(...)`. A
column that was never captured is never populated, so it would be a silent `null` at runtime; the usual cause is
binding the wrong generated projection (for example one copied from another step). A capture-all image
(`WithPostImage()` with no columns) is exempt.

**Fix:** use the projection generated for this step, or capture the missing column in `WithPreImage`/`WithPostImage`.
See [Typed images](../guides/plugins.md#typed-images-compile-safe-projections).
