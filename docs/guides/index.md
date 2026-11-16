---
_disableAffix: true
---

# Guides

Task-focused guides for building, testing and shipping Dataverse plugins and Custom APIs with Vela, grouped by what
you are doing. New to Vela? Follow [Install](../getting-started/install.md) and the
[Quickstart](../getting-started/quickstart.md), or walk through the [end-to-end example](end-to-end.md).

## Build plugins

<div class="vela-cards">
  <a class="vela-card" href="plugins.md"><span class="t">Write a plugin</span><span class="d">Declare steps, handle them with a typed context, use images and context helpers.</span></a>
  <a class="vela-card" href="custom-apis.md"><span class="t">Write a Custom API</span><span class="d">Declarative Custom API definitions with typed request and response parameters.</span></a>
  <a class="vela-card" href="queries.md"><span class="t">Typed queries</span><span class="d">Read records early-bound with Retrieve&lt;T&gt; and Query.For&lt;T&gt;().</span></a>
  <a class="vela-card" href="models.md"><span class="t">Early-bound models (<code>vela models</code>)</span><span class="d">Generate C# entity and message classes from your environment.</span></a>
  <a class="vela-card" href="dependency-injection.md"><span class="t">Dependency injection</span><span class="d">The lightweight, sandbox-safe DI container.</span></a>
  <a class="vela-card" href="escape-hatches.md"><span class="t">Escape hatches</span><span class="d">Drop to the raw Dataverse SDK when a helper does not cover a case.</span></a>
</div>

## Built-in services

<div class="vela-cards">
  <a class="vela-card" href="caching.md"><span class="t">Caching</span><span class="d">TTL cache with refresh for plugin data.</span></a>
  <a class="vela-card" href="environment-variables.md"><span class="t">Environment variables</span><span class="d">Read Dataverse environment variables from a plugin, typed.</span></a>
  <a class="vela-card" href="localization.md"><span class="t">Localized messages</span><span class="d">Business errors in the user's language, by message key.</span></a>
  <a class="vela-card" href="managed-identity.md"><span class="t">Managed identity</span><span class="d">Call external APIs with a managed-identity token, no stored secret.</span></a>
  <a class="vela-card" href="telemetry.md"><span class="t">Telemetry and Application Insights</span><span class="d">Emit leveled, structured telemetry from a plugin handler.</span></a>
</div>

## Test

<div class="vela-cards">
  <a class="vela-card" href="testing.md"><span class="t">Testing</span><span class="d">Run plugins through PluginPipeline, which simulates the Dataverse pipeline in memory.</span></a>
  <a class="vela-card" href="security-model.md"><span class="t">Security model in tests</span><span class="d">Roles, privileges, business units, sharing and cascades.</span></a>
</div>

## CLI and deployment

<div class="vela-cards">
  <a class="vela-card" href="cli-overview.md"><span class="t">The vela CLI</span><span class="d">Commands, exit codes, proxy, solution prefix and execution logs.</span></a>
  <a class="vela-card" href="scaffold.md"><span class="t">Scaffold a solution (<code>vela init</code>)</span><span class="d">Generate a complete, build-ready solution.</span></a>
  <a class="vela-card" href="add.md"><span class="t">Add a component (<code>vela add</code>)</span><span class="d">Generate a plugin, service, Custom API or test, or add a step to a plugin.</span></a>
  <a class="vela-card" href="plan-apply.md"><span class="t">Plan and apply</span><span class="d">Preview, then apply registrations into an unmanaged solution.</span></a>
  <a class="vela-card" href="signing.md"><span class="t">Sign the plugin package (<code>vela sign</code>)</span><span class="d">Sign a plugin package for managed-identity plugins.</span></a>
  <a class="vela-card" href="client-side.md"><span class="t">Client-side (preview)</span><span class="d">Work in progress: TypeScript form and ribbon scripts and web resources. May change.</span></a>
</div>

## Examples and troubleshooting

<div class="vela-cards">
  <a class="vela-card" href="end-to-end.md"><span class="t">End-to-end example</span><span class="d">One plugin from code to test, deployment and its trace log.</span></a>
  <a class="vela-card" href="cookbook.md"><span class="t">Cookbook</span><span class="d">Short, compiled recipes for the most common patterns.</span></a>
  <a class="vela-card" href="migrating.md"><span class="t">Migrating an existing project</span><span class="d">Convert existing plugins and workflow activities to Vela.</span></a>
  <a class="vela-card" href="troubleshooting.md"><span class="t">Troubleshooting</span><span class="d">Error messages and their fixes.</span></a>
</div>
