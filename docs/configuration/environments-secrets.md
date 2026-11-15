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

A step's **secure configuration** (declared in code with `WithSecureConfigFrom("Name")`) is a deployment secret
too: pass it to `plan`/`apply` with `--secure-config Name=@file` (or `Name=VALUE`), or set the
`VELA_SECURE_CONFIG_<Name>` environment variable in the pipeline. Without a value, `apply` leaves the environment's
own secure configuration untouched. See [Write a plugin](../guides/plugins.md).

In a pipeline, map the secret store to `DATAVERSE_CLIENT_SECRET` and keep only non-secret values on the command
line:

```bash
# DATAVERSE_CLIENT_SECRET and VELA_SECURE_CONFIG_<Name> come from the pipeline's secret store
vela apply --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --auto-approve
```

The full set of `plan` / `apply` commands is on [Plan & Apply](../guides/plan-apply.md).

## CI strict mode

In CI, add `--strict` to make the run fail (exit 4) when there is any warning in `diagnostics` (rank collisions, a
secure configuration without a value, a Custom API name without the publisher prefix). Information such as an
assembly not yet in Dataverse on a first deployment is reported under `info` and never fails it. Add
`--detailed-exitcode` to `plan` to get exit `8` when there are changes to apply:

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --strict --detailed-exitcode
```

The same strictness is available at the scanner level. `FrameworkManifestScanner` (namespace `Vela.Manifest`)
takes a `Strict = true` option that makes the scan throw when it reports any diagnostic:

```csharp
var manifest = FrameworkManifestScanner.Scan(
    typeof(AccountPostUpdate).Assembly,
    new FrameworkManifestScanOptions { Strict = true });
```

## Labelling the environment

`plan`, `apply` and `inspect` accept an optional `--environment <label>` that names the target in the summary and
JSON output (the heading reads, for example, `plan production`). It is a display label only, useful when a
pipeline runs the same command against several environments; when omitted, the output falls back to the
connection `--url`. (`--solution-unique-name` is also accepted as an alias of `--solution`.)

## Output: terminal vs piped

`plan`, `apply`, and `inspect` print a colorized, human-readable summary when stdout is a terminal, and JSON
when the output is piped or redirected (as it is in CI). This means the same command gives a readable preview
at the keyboard and a machine-readable contract in a pipeline, with no flag change:

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

To force JSON at a terminal (for example, to pipe into `jq` or save it), pass `--json` or `--out <file>`. The
fields and exit codes are documented in [Machine-readable output](../reference/machine-readable-output.md). To
turn colors off, set `NO_COLOR` or `VELA_NO_COLOR`.

Add `--verbose` for full exception stack traces on failure (and the human summary on stderr alongside the JSON
when output is piped):

```bash
vela apply --packages-dir ./artifacts --solution ZavaSales --interactive --verbose
```
