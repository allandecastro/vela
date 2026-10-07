# Machine-readable output

`vela plan`, `vela apply` and `vela inspect` write a JSON document that scripts, CI pipelines and coding agents can
rely on. This page is the contract: the fields, what they mean, and the exit codes.

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
adds detail on stderr and never removes the JSON. `vela init --json` follows the same rule.

## Versioning

Every document starts with `"schemaVersion": 1`. The version changes only when an existing field changes meaning or
shape, or is removed. A new field is added without a version change, so read the fields you need and ignore the rest.

## plan and apply

```json
{
  "schemaVersion": 1,
  "mode": "plan",
  "environment": "https://zava.crm.dynamics.com",
  "hasChanges": true,
  "summary": { "added": 1, "removed": 0, "changed": 0 },
  "operations": [
    { "action": "create", "kind": "step", "type": "Zava.Plugins.AccountPlugin", "reason": "Exists locally but not in Dataverse." },
    { "action": "update", "kind": "package", "type": "zava_Plugins", "reason": "Plugin package version or content differs from Dataverse; apply will republish it." },
    { "action": "add-to-solution", "kind": "custom-api", "type": "zava_DoThing", "reason": "Exists in Dataverse but is not in the target solution; apply will add it." }
  ],
  "diff": {
    "localAssemblies": ["Zava.Plugins"],
    "currentAssemblies": ["Zava.Plugins"],
    "added": [ { "kind": "step", "type": "Zava.Plugins.AccountPlugin", "definition": { } } ],
    "removed": [],
    "changed": [ { "kind": "custom-api", "type": "Zava.Plugins.MyApi", "before": { }, "after": { } } ],
    "localItems": [],
    "currentItems": [],
    "summary": { "added": 1, "removed": 0, "changed": 1 }
  },
  "diagnostics": [],
  "notes": ["Compared local .nupkg plugin metadata with live Dataverse state.", "This is a preview; nothing was written to Dataverse."],
  "unmanaged": [],
  "info": ["Plugin assembly 'Zava.Plugins' not found in Dataverse (first deployment); apply will create it."]
}
```

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Contract version (see above). Always the first property. |
| `mode` | `plan`; for `apply`: `apply-execute` (finished, including "no changes") or `apply-failed` (stopped mid-run, exit 7). |
| `environment` | `--environment`, else `--url`. |
| `hasChanges` | `true` when apply would write anything: a registration change, a package publish, or a component to add to `--solution`. Use this, not `summary`, to decide whether to apply. |
| `summary` | Counts of the registration diff (steps, Custom APIs, and web resources on a `--webresources` run). It excludes package and solution-membership operations. After a successful apply it is what remains (normally all zero). |
| `operations[]` | Every write apply would perform. `action` is `create`, `update`, `delete` or `add-to-solution`; `kind` is `step`, `custom-api`, `web-resource`, `package`, `assembly`, `custom-api-parameter` or `custom-api-response-property`; `type` is the plugin type, unique name or package name. |
| `diff` | The full before/after definitions. `added` / `removed` items carry `definition`; `changed` items carry `before` and `after`. Secure configuration values appear only as a keyed digest, never in clear. |
| `diagnostics[]` | Warnings: rank collisions, a declared secure configuration without a value, a Custom API name without the solution's publisher prefix. `--strict` fails (exit 4) when this array is not empty. |
| `info[]` | Information that is not a problem, such as an assembly not yet in Dataverse on a first deployment. Never fails `--strict`. |
| `unmanaged[]` | Plugin types in the deployed assembly that Vela does not own (classic `IPlugin` / `CodeActivity`); their registrations are left untouched. Information, not a warning. |
| `notes[]` | Free text for humans. Do not parse it. |
| `executed` | `apply` only: what the run did, from the pre-apply diff. |

`executed` on `apply-execute`:

```json
"executed": {
  "created": 2, "updated": 1, "deleted": 0,
  "packageRepublished": true,
  "addedToSolution": 3,
  "byKind": [ { "kind": "step", "created": 2, "updated": 1, "deleted": 0 } ]
}
```

On `apply-failed`, the document is the pre-apply plan with mode `apply-failed` and the failure in `notes`. The
per-operation report (completed / failed / not applied) is on stderr; re-run `apply` to converge.

## inspect

```json
{
  "schemaVersion": 1,
  "mode": "inspect",
  "environment": "https://zava.crm.dynamics.com",
  "local":   { "assemblies": ["Zava.Plugins"], "steps": [ { "kind": "step", "type": "...", "definition": { } } ], "customApis": [] },
  "current": { "assemblies": [], "steps": [], "customApis": [] },
  "unmanaged": [],
  "diagnostics": [],
  "info": ["Plugin assembly 'Zava.Plugins' not found in Dataverse (first deployment); apply will create it."]
}
```

## The command table

`vela help --json` describes every command, its options (name, aliases, `kind`: `flag`, `value` or
`optional-value`, value name, description) and the exit codes. It is generated from the same table that validates
the options, so an agent can discover the CLI without parsing help text.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success (for `plan`, also "changes found" unless `--detailed-exitcode`) |
| `2` | Invalid arguments (unknown option, missing value, option of another command) |
| `3` | File not found (package, folder, secure-config file) |
| `4` | Error, including a `--strict` failure and a refused non-interactive `apply` without `--auto-approve` |
| `5` | Drift detected (`vela models --verify`) |
| `6` | Dataverse connection failure |
| `7` | Partial apply: the run stopped mid-way; Dataverse may be partly updated. Re-run `apply`. |
| `8` | `vela plan --detailed-exitcode` only: the plan found changes to apply |

A typical CI gate:

```bash
vela plan --packages-dir artifacts --solution ZavaSales --strict --json --detailed-exitcode > plan.json
case $? in
  0) echo "nothing to deploy" ;;
  8) vela apply --packages-dir artifacts --solution ZavaSales --auto-approve --json > apply.json ;;
  *) exit 1 ;;
esac
```
