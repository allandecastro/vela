---
_description: "Run the CLI unattended: passing secrets, strict mode, output in pipelines"
---

# Environments and CI

This page covers running the `vela` CLI unattended: passing secrets safely, failing the build on warnings, labelling
the target environment, and reading the output in a pipeline. It assumes you have chosen a connection method; see
[Authentication](authentication.md).

## Pass secrets safely

### The client secret

An app-registration login needs a client secret. Supply it, in order of preference:

1. the `DATAVERSE_CLIENT_SECRET` environment variable;
2. `--client-secret-stdin`, with the secret piped in;
3. `--client-secret <secret>`, which works but warns, because the value is visible in shell history and process
   listings.

A managed identity or workload identity with `--url` alone needs no secret at all; see
[Ambient Azure credential](authentication.md#ambient-azure-credential).

### Step secure configuration

A step's secure configuration (declared in code with `WithSecureConfigFrom("Name")`) is a deployment secret too. Pass
it to `plan` and `apply` with `--secure-config Name=@file`, or set `VELA_SECURE_CONFIG_<Name>` in the pipeline. An
inline `--secure-config Name=VALUE` works but warns. Without a value, `apply` leaves the environment's own secure
configuration untouched. An empty variable or an empty file counts as no value and prints a warning, so a secret
missing from the pipeline's store never clears the live one; only an explicit `--secure-config Name=` clears it.
See [Plan and apply](../guides/plan-apply.md#secure-configuration).

### Example

Map the secret store to environment variables and keep only non-secret values on the command line:

```bash
# DATAVERSE_CLIENT_SECRET and VELA_SECURE_CONFIG_<Name> come from the pipeline's secret store
vela apply --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --auto-approve
```

`apply` needs `--auto-approve` (or `--yes`) whenever stdin is not a terminal: without it, it refuses with exit `4`
and writes nothing.

## Fail the build on warnings (`--strict`)

Add `--strict` to make `plan` or `apply` exit `4` when `diagnostics` holds any warning: rank collisions, or a declared
secure configuration without a value. Information, such as an assembly that is not in Dataverse yet on a first
deployment, goes to `info` and never fails `--strict`. Errors that would make `apply` fail, such as a Custom API name
without the publisher prefix, stop `plan` and `apply` with exit `4` with or without `--strict` (see
[Plan and apply](../guides/plan-apply.md#errors-that-stop-the-preview)).

Add `--detailed-exitcode` to `plan` to get exit `8` when there are changes to apply, and `0` when there is nothing to
do:

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --strict --detailed-exitcode
```

A complete gate that applies only when the plan has changes is in
[Machine-readable output](../reference/machine-readable-output.md#exit-codes).

The same check exists in code: `FrameworkManifestScanner` takes `Strict = true` to throw on any diagnostic; see
[Read the registration manifest in code](../guides/plan-apply.md#read-the-registration-manifest-in-code).

## Label the environment (`--environment`)

`plan`, `apply` and `inspect` accept `--environment <label>`, which names the target in the summary and in the JSON
`environment` field (the heading reads, for example, `plan production`). It is a display label only, useful when a
pipeline runs the same command against several environments. Without it, the output shows `--url`. The `apply`
confirmation prompt names the label and the URL of the organization the connection reached, so a label cannot hide
which environment is about to be written.

## Read the output in a pipeline

`plan`, `apply` and `inspect` print a colored summary when stdout is a terminal, and JSON when stdout is piped or
redirected, as it is in most CI runners. The same command gives a readable preview at the keyboard and a
machine-readable document in a pipeline.

- `--json` forces JSON on a terminal, and `--out <file>` writes the JSON to a file.
- `NO_COLOR` or `VELA_NO_COLOR` turns colors off.
- `--verbose` (`-v`) adds progress lines and full stack traces on stderr, and, when stdout carries JSON, the human
  summary on stderr too. It never changes stdout.

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --json --verbose > plan.json
```

The JSON fields and the exit codes are documented in
[Machine-readable output](../reference/machine-readable-output.md).
