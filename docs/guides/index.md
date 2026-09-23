---
_disableAffix: true
---

# Guides

Task-focused guides for building, testing and shipping Dataverse plugins and Custom APIs with Vela, grouped
by what you are doing. New here? Follow [Install](../getting-started/install.md) then
[Quickstart](../getting-started/quickstart.md), or walk the [end-to-end example](end-to-end.md).

## Scaffold & generate

<div class="vela-cards">
  <a class="vela-card" href="cli-overview.md"><span class="t">The vela CLI</span><span class="d">Commands, exit codes, install options and secret handling.</span></a>
  <a class="vela-card" href="scaffold.md"><span class="t">Scaffold a solution</span><span class="d">Generate a complete, build-ready solution with vela init.</span></a>
  <a class="vela-card" href="add.md"><span class="t">Add a file (vela add)</span><span class="d">Generate a plugin, service, Custom API, test or step.</span></a>
  <a class="vela-card" href="models.md"><span class="t">Early-bound & typed models</span><span class="d">Generate C# entity/message classes with vela models.</span></a>
</div>

## Author

<div class="vela-cards">
  <a class="vela-card" href="plugins.md"><span class="t">Write a plugin</span><span class="d">Steps-first base types, context helpers and configuration.</span></a>
  <a class="vela-card" href="custom-apis.md"><span class="t">Write a Custom API</span><span class="d">Declarative Custom API definitions and parameters.</span></a>
  <a class="vela-card" href="queries.md"><span class="t">Typed queries</span><span class="d">Strongly-typed QueryExpression helpers and ToFetchXml().</span></a>
  <a class="vela-card" href="dependency-injection.md"><span class="t">Dependency injection</span><span class="d">The lightweight, sandbox-safe DI container.</span></a>
  <a class="vela-card" href="caching.md"><span class="t">Caching</span><span class="d">TTL cache with refresh for plugin data.</span></a>
</div>

## Built-in services

<div class="vela-cards">
  <a class="vela-card" href="environment-variables.md"><span class="t">Environment variables</span><span class="d">Read Dataverse environment variables from a plugin, typed.</span></a>
  <a class="vela-card" href="managed-identity.md"><span class="t">Managed identity</span><span class="d">Call external APIs with a managed-identity token, no stored secret.</span></a>
  <a class="vela-card" href="telemetry.md"><span class="t">Telemetry & Application Insights</span><span class="d">Emit leveled, structured telemetry from a plugin handler.</span></a>
  <a class="vela-card" href="escape-hatches.md"><span class="t">Escape hatches</span><span class="d">Drop to the raw Dataverse SDK when a helper does not cover a case.</span></a>
</div>

## Test

<div class="vela-cards">
  <a class="vela-card" href="testing.md"><span class="t">Testing</span><span class="d">Run plugins against an in-memory Dataverse, no connection.</span></a>
  <a class="vela-card" href="security-model.md"><span class="t">Security model in tests</span><span class="d">Roles, privileges, business units, sharing and cascades.</span></a>
</div>

## Deploy

<div class="vela-cards">
  <a class="vela-card" href="plan-apply.md"><span class="t">Plan & Apply</span><span class="d">Preview then apply registrations against a live environment.</span></a>
  <a class="vela-card" href="signing.md"><span class="t">Signing</span><span class="d">Sign a plugin package for managed-identity plugins.</span></a>
  <a class="vela-card" href="client-side.md"><span class="t">Client-side (preview)</span><span class="d">TypeScript form/ribbon scripts and web resources for vela-client.</span></a>
</div>

## Learn more

<div class="vela-cards">
  <a class="vela-card" href="end-to-end.md"><span class="t">End-to-end example</span><span class="d">A full walkthrough from code to a running step.</span></a>
  <a class="vela-card" href="cookbook.md"><span class="t">Cookbook</span><span class="d">Common recipes at a glance.</span></a>
  <a class="vela-card" href="troubleshooting.md"><span class="t">Troubleshooting</span><span class="d">Fixes for the most common issues.</span></a>
</div>
