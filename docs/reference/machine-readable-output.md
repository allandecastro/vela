---
_description: "The versioned JSON contract of plan, apply and inspect, and the exit codes"
---

# Machine-readable output

`vela plan`, `vela apply` and `vela inspect` write a versioned JSON document that scripts, CI pipelines and coding
agents can rely on. This page is that contract: when you get JSON, what each field means, the command table of
`vela help --json`, and the exit codes of every command.

`init`, `add`, `models`, `logs`, `solution show` and `sign` also accept `--json` and print a JSON report, but those
reports carry no `schemaVersion` and are not part of this contract; `vela <command> --help` describes them.

## When you get JSON

| Situation | stdout | stderr |
| --- | --- | --- |
| Terminal, no `--json`, no `--out` | Colored human summary | Warnings, notices |
| Piped or redirected stdout | JSON document | Warnings, notices |
| `--json` | JSON document | Warnings, notices |
| `--out <file>` | Nothing (the document goes to the file) | `Plan written: <path>`, warnings, notices |
| `--verbose` with any of the above | Unchanged | Also the human summary, progress lines and stack traces |

stdout never carries anything but the result. Warnings (for example `--client-secret` on the command line), progress
lines, the confirmation prompt, the resume report of a failed apply and update notices all go to stderr. `--verbose`
adds detail on stderr and never removes the JSON.

## Versioning

Every document starts with `"schemaVersion": 1`. The version changes only when an existing field changes meaning or
shape, or is removed. A new field is added without a version change, so read the fields you need and ignore the rest.

## `plan` and `apply`

```json
{
  "schemaVersion": 1,
  "mode": "plan",
  "environment": "https://zava.crm.dynamics.com",
  "hasChanges": true,
  "summary": { "added": 1, "removed": 0, "changed": 0 },
  "operations": [
    { "action": "add-to-solution", "kind": "custom-api", "type": "zava_DoThing", "reason": "Exists in Dataverse but is not in the target solution; apply will add it." },
    { "action": "update", "kind": "package", "type": "zava_Zava.Plugins", "reason": "Plugin package version or content differs from Dataverse; apply will republish it." },
    { "action": "create", "kind": "step", "type": "Zava.Plugins.AccountPlugin", "reason": "Exists locally but not in Dataverse." }
  ],
  "diff": {
    "localAssemblies": ["Zava.Plugins"],
    "currentAssemblies": ["Zava.Plugins"],
    "added": [ { "kind": "step", "type": "Zava.Plugins.AccountPlugin", "definition": { } } ],
    "removed": [],
    "changed": [],
    "localItems": [],
    "currentItems": [],
    "summary": { "added": 1, "removed": 0, "changed": 0 }
  },
  "diagnostics": [],
  "notes": ["Compared local .nupkg plugin metadata with live Dataverse state.", "This is a preview; nothing was written to Dataverse."],
  "unmanaged": [],
  "info": []
}
```

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Contract version (see above). Always the first property. |
| `mode` | `plan`; for `apply`: `apply-execute` (finished, including "no changes"), `apply-not-converged` (every write ran but the environment still differs; `summary` and `diff` show what remains, exit 5) or `apply-failed` (stopped mid-run, exit 7). |
| `environment` | `--environment`, else `--url`. |
| `hasChanges` | `true` when apply would write anything: a registration change, a package publish, or a component to add to `--solution`. Use this, not `summary`, to decide whether to apply. |
| `summary` | Counts of the registration diff (steps, Custom APIs, and web resources on a `--webresources` run): the same numbers as `diff.summary` and the human `summary: +N -N ~N` line. It deliberately excludes the `package` operations, every `add-to-solution` operation and the managed-identity `link` operations, so it can be lower than the number of `operations[]` (above, `added` is 1 for three operations), and it can be all zero while `hasChanges` is `true` (a code-only rebuild, a pending link). Count `operations[]` by `action` and `kind` for every write. After a successful apply it is what remains (normally all zero), which is how `apply-not-converged` is decided. |
| `operations[]` | Every write apply would perform. `action` is `create`, `update`, `delete`, `add-to-solution` or `link` (a `package` linked to `--managed-identity`); `kind` is `step`, `custom-api`, `web-resource`, `package`, `assembly`, `custom-api-parameter` or `custom-api-response-property`; `type` is the plugin type, unique name or package name. |
| `diff` | The full before/after definitions. `added` / `removed` items carry `definition`; `changed` items carry `before` and `after`. Secure configuration values appear only as a keyed digest, never in clear. A local step scoped with `ForRelationship(...)` carries `relationships` (the relationship schema names). It is absent from live definitions and from steps without a scope, and it is not compared (see [Plan and apply](../guides/plan-apply.md)). |
| `diagnostics[]` | Warnings: rank collisions, a declared secure configuration without a value. `--strict` fails (exit 4) when this array is not empty. A problem that would make `apply` fail (a Custom API name without the solution's publisher prefix, a step whose message or table filter does not exist) is not a diagnostic: `plan` and `apply` exit 4 without a JSON document (see [Plan and apply](../guides/plan-apply.md#errors-that-stop-the-preview)). |
| `info[]` | Information that is not a problem, such as an assembly not yet in Dataverse on a first deployment. Never fails `--strict`. |
| `unmanaged[]` | Plugin types in the deployed assembly that Vela does not own (classic `IPlugin` / `CodeActivity`); their registrations are left untouched. Information, not a warning. |
| `notes[]` | Free text for humans. Do not parse it. |
| `executed` | `apply` only: what the run did, from the pre-apply diff. |
| `managedIdentity` | Only with `--managed-identity`: `id` (the option) and `packages[]`, one per plugin package the run publishes, with `package` (its name in Dataverse), `status` (`linked`: already that managed identity; `link`: not linked yet, or created by this apply; `change`: linked to another one) and, for `change`, `currentManagedIdentityId`. A `link` or `change` package is also a `link` operation and counts in `hasChanges`. After an apply, it is the state read back (normally all `linked`). |

`executed` on `apply-execute`:

```json
"executed": {
  "created": 2, "updated": 1, "deleted": 0,
  "packageRepublished": true,
  "addedToSolution": 3,
  "linkedToManagedIdentity": 0,
  "byKind": [ { "kind": "step", "created": 2, "updated": 1, "deleted": 0 } ]
}
```

`managedIdentity` with `--managed-identity`:

```json
"managedIdentity": {
  "id": "0b9a3c1e-2222-4a2b-9c3d-000000000002",
  "packages": [
    { "package": "zava_Zava.Integrations", "status": "change", "currentManagedIdentityId": "0b9a3c1e-1111-4a2b-9c3d-000000000001" }
  ]
}
```

On `apply-failed`, the document is the pre-apply plan with mode `apply-failed` and the failure in `notes`. The
per-operation report (completed / failed / not applied) is on stderr; re-run `apply` to converge.

## `inspect`

```json
{
  "schemaVersion": 1,
  "mode": "inspect",
  "environment": "https://zava.crm.dynamics.com",
  "local":   { "assemblies": ["Zava.Plugins"], "steps": [ { "kind": "step", "type": "...", "definition": { } } ], "customApis": [] },
  "current": { "assemblies": [], "steps": [], "customApis": [] },
  "unmanaged": [],
  "diagnostics": [],
  "info": ["Plugin assembly 'Zava.Plugins' not found in Dataverse (first deployment)."]
}
```

## The command table

`vela help --json` describes the CLI for agents and completion scripts, without parsing help text:

- `schemaVersion`, `tool` and `version`;
- `commands[]`: `name`, `aliases`, `summary`, `usage` and `options[]`, where each option has `name`, `aliases`,
  `kind` (`flag`, `value` or `optional-value`), `value` (the value placeholder) and `description`;
- `exitCodes`: each code with a short description.

It is generated from the same table that validates the options, so it lists exactly what each command accepts.

## Exit codes

Every `vela` command returns one of these codes. This table is the reference; other pages summarize it.

| Code | Meaning |
| --- | --- |
| `0` | Success. For `plan`, also when it finds changes, unless `--detailed-exitcode` is set. For `apply`, also when you answer anything but `yes` at the prompt (`Apply cancelled.`). |
| `2` | Invalid arguments: an unknown option, an option of another command, a missing value, missing authentication options, a `--url` that is not an absolute `https` URL, `plan` or `apply` without `--solution`, `add customapi` without `--prefix`. |
| `3` | A file or folder was not found: a package, a packages folder, a web resources folder, a `--secure-config` file, the package or PFX to sign. |
| `4` | Error: a `--strict` failure; an `apply` refused because stdin is not a terminal and `--auto-approve` is missing; a `plan` or `apply` `--solution` that is missing or managed in the target environment; a `plan` or `apply` stopped in the preview, before any write, by a Custom API name without the publisher prefix or a step whose message or table filter does not exist; a failed `dotnet nuget sign`; any other unexpected failure. |
| `5` | Drift detected: `vela models --verify` found the model on disk out of date, or `apply` wrote every change but the environment still differs (mode `apply-not-converged`). |
| `6` | Dataverse connection failure: authentication, the endpoint, or a network error (DNS, socket, HTTP, timeout) on any call, including while reading the live state. |
| `7` | Partial apply: the run stopped partway (mode `apply-failed`) and Dataverse may be partly updated. Fix the cause and run `apply` again. |
| `8` | `vela plan --detailed-exitcode` only: the plan found changes to apply. |

A typical CI gate, with the connection taken from `DATAVERSE_CONNECTION_STRING`:

```bash
vela plan --packages-dir artifacts --solution ZavaSales --strict --json --detailed-exitcode > plan.json
case $? in
  0) echo "nothing to deploy" ;;
  8) vela apply --packages-dir artifacts --solution ZavaSales --auto-approve --json > apply.json ;;
  *) exit 1 ;;
esac
```
