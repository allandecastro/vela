# Environments & CI

This page covers running the `vela` CLI unattended: how to pass secrets safely, how to
make a run fail the build on diagnostics (strict mode), and the `--verbose` flag. For
the full set of connection options, see [Authentication](authentication.md).

## Passing secrets: environment variable vs flag

An app-registration login needs a client secret. Supply it one of three ways:

- `DATAVERSE_CLIENT_SECRET` environment variable - preferred,
- `--client-secret-stdin` - preferred (pipe the value in),
- `--client-secret <client-secret>` - discouraged.

> [!WARNING]
> Prefer the `DATAVERSE_CLIENT_SECRET` environment variable or `--client-secret-stdin`
> over `--client-secret`, which is exposed to shell history and process listings (the
> CLI warns when you use it).

The unattended `plan` / `apply` invocations take the same connection flags:

```powershell
# Compare local package(s) with live Dataverse state (client id / client secret)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# Execute apply operations (idempotent upsert/delete) into an existing Dataverse solution
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --solution ZavaSales `
  --execute
```

## CI strict mode

In CI, add `--strict` to make the run fail when scanner/metadata diagnostics exist:

```powershell
# CI strict mode (fail if scanner/metadata diagnostics exist)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --strict
```

The same strictness is available at the scanner level. `FrameworkManifestScanner`
takes a `Strict = true` option so scanner diagnostics fail the build:

```csharp
var manifest = FrameworkManifestScanner.Scan(
    typeof(AccountPostUpdate).Assembly,
    new FrameworkManifestScanOptions { Strict = true });
```

## Output: terminal vs piped

`plan`, `apply`, and `inspect` print a colorized, human-readable summary when stdout is a terminal, and JSON
when the output is piped or redirected (as it is in CI). This means the same command gives a readable preview
at the keyboard and a machine-readable contract in a pipeline, with no flag change:

```powershell
vela plan --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive
```

To force JSON at a terminal (for example, to pipe into `jq` or save it), pass `--json` or `--out <file>`. To
turn colors off, set `NO_COLOR` or `VELA_NO_COLOR`.

Add `--verbose` for full exception stack traces on failure (and the human summary on stderr alongside the JSON
when output is piped):

```powershell
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --solution ZavaSales --interactive --execute --verbose
```
