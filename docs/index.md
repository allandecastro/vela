---
_disableToc: true
_disableAffix: true
_disableBreadcrumb: true
---

<div class="vela-hero">
  <img class="vela-hero-logo light-only" src="images/vela-lockup.svg" alt="Vela" />
  <img class="vela-hero-logo dark-only" src="images/vela-lockup-dark.svg" alt="Vela" />
  <p class="vela-hero-tagline">Build Dataverse with confidence.</p>
  <p class="vela-hero-sub">A framework and CLI for Microsoft Dataverse and Dynamics 365 plugins and Custom APIs: a lightweight plugin runtime, a scriptable <code>vela</code> CLI, and a connection-free in-memory test harness.</p>
  <p class="vela-hero-actions">
    <a class="vela-btn" href="getting-started/install.md">Get started</a>
    <a class="vela-btn-ghost" href="getting-started/quickstart.md">Quickstart</a>
  </p>
</div>

<p class="vela-hero-for">For Dataverse and Dynamics 365 developers who write plugins and Custom APIs in C#.</p>

## See it

Register a plugin step with a strongly-typed handler, preview it, apply it, then test it with no connection and no mocking library:

[!code-csharp[](samples/Vela.DocSamples/HomePageSample.cs#plugin)]

```powershell
vela plan  --packages-dir ./artifacts --solution ZavaSales --interactive   # preview the registration (read-only)
vela apply --packages-dir ./artifacts --solution ZavaSales --interactive   # upsert it into your solution
```

[!code-csharp[](samples/Vela.DocSamples/HomePageSample.cs#test)]

## Why Vela

<div class="vela-cards">
  <a class="vela-card" href="guides/plugins.md"><span class="t">Fluent plugins</span><span class="d">Declarative step registration and strongly-typed handlers, with typed target, images, tracing and a TTL cache.</span></a>
  <a class="vela-card" href="guides/custom-apis.md"><span class="t">Custom APIs</span><span class="d">Declarative Custom API definitions and typed request/response parameters.</span></a>
  <a class="vela-card" href="guides/plan-apply.md"><span class="t">Preview-first CLI</span><span class="d">Idempotent plan then apply of steps, Custom APIs and images against a live environment.</span></a>
  <a class="vela-card" href="guides/testing.md"><span class="t">No-connection testing</span><span class="d">Run plugins against an in-memory Dataverse - no live connection, no mocking library.</span></a>
  <a class="vela-card" href="guides/models.md"><span class="t">Early-bound & typed models</span><span class="d">Generate C# entity classes and typed message classes from your environment.</span></a>
  <a class="vela-card" href="guides/security-model.md"><span class="t">Opt-in security model</span><span class="d">Roles, privileges, business units, teams, sharing and cascades when a test needs them.</span></a>
</div>

## Start here

<div class="vela-cards">
  <a class="vela-card" href="getting-started/install.md"><span class="t">Install</span><span class="d">Get the vela CLI as a .NET global tool.</span></a>
  <a class="vela-card" href="getting-started/quickstart.md"><span class="t">Quickstart</span><span class="d">From an empty folder to a registered, tested plugin in five steps.</span></a>
  <a class="vela-card" href="getting-started/initialize-a-solution.md"><span class="t">Initialize a solution</span><span class="d">What vela init scaffolds and where to go next.</span></a>
</div>
