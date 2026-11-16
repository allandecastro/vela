# CLI cheat sheet

Every `vela` command and option on one page. For how the commands fit together, see
[The vela CLI](../guides/cli-overview.md); for deployment, see [Plan and apply](../guides/plan-apply.md). The same
table is available offline with `vela help <command>`, and as JSON with `vela help --json`.

Options marked *(preview)* belong to the client-side preview and may change.

## Commands

| Command | Purpose | Options |
| --- | --- | --- |
| `vela init` (alias `new`) | Scaffold a build-ready solution. See [Scaffold a solution](../guides/scaffold.md). | `--name` (or first argument), `--prefix`, `--publisher-prefix`, `--solution`, `--output` / `-o`, `--samples` (with it: `--no-console`, `--no-integrations`), `--minimal` / `--no-demos` (the default), `--integrations`, `--managed-identity` / `--mi` (implies `--integrations`), `--no-tests`, `--core-version`, `--force`, `--web-resources` / `--webresources`, `--web-resources-namespace`, `--web-resources-folder` *(preview)*, `--interactive` / `-i`, `--json`, `--verbose` / `-v` |
| `vela add <kind> <Name>` | Add one file, or a step, to a project. See [Add a file](../guides/add.md). | Kinds: `plugin`, `service`, `customapi`, `test`, `step`, `form-script` and `ribbon-script` *(preview)*. Options: `--entity` / `-e`, `--message`, `--stage`, `--mode`, `--filtering-attributes` / `--filter`, `--handler`, `--kind` (`test`), `--bound` / `--global`, `--function` / `--action`, `--prefix`, `--project` / `-p`, `--force`, `--interactive` / `-i`, `--json`. Each kind accepts only the options it uses. |
| `vela models <scope>` | Generate early-bound classes, typed messages or roles. See [Early-bound models](../guides/models.md). | [Scopes](#models-scopes), [connection options](#connection-options) |
| `vela plan` | Preview the changes. Read-only. | [Package inputs](#package-inputs), [connection options](#connection-options), `--solution` (required), `--secure-config`, `--managed-identity`, `--strict`, `--detailed-exitcode`, `--webresources` *(preview)*, `--environment`, `--json`, `--out`, `--verbose` / `-v` |
| `vela apply` | Apply the changes. | [Package inputs](#package-inputs), [connection options](#connection-options), `--solution` (required), `--secure-config`, `--managed-identity`, `--auto-approve` / `--yes`, `--batch-size`, `--strict`, `--webresources` *(preview)*, `--environment`, `--json`, `--out`, `--verbose` / `-v` |
| `vela inspect` | Show local and live registrations side by side. Read-only. | [Package inputs](#package-inputs), [connection options](#connection-options), `--solution` (optional; required with `--webresources`), `--secure-config`, `--webresources` *(preview)*, `--environment`, `--json`, `--out`, `--verbose` / `-v` |
| `vela solution show <uniquename>` | Print a solution's publisher prefix. Read-only. | [Connection options](#connection-options), `--json`, `--verbose` / `-v` |
| `vela logs` (alias `trace`) | Read the Plug-in Trace Log, or failed system jobs. Read-only. See [Reading execution logs](../guides/cli-overview.md#reading-execution-logs-vela-logs). | `--jobs`, `--all-statuses`, `--errors`, `--top`, `--since`, `--type`, `--message`, `--entity`, `--correlation`, `--full`, `--json`, `--verbose` / `-v`, [connection options](#connection-options) |
| `vela sign` | Sign a plugin package. See [Signing](../guides/signing.md). | `--package`, one certificate source (`--fingerprint`, `--pfx` or `--pfx-base64-env`), a password for a PFX (`--pfx-password-env` or `--pfx-credential`), `--timestamp`, `--json`. `--pfx-base64` and `--pfx-password` also work but warn. |
| `vela logout` | Remove the cached interactive token and the remembered URL. | |
| `vela update` | Update the global tool. | `--version <x.y.z>` |
| `vela menu` (or bare `vela`) | Open the interactive menu. | |
| `vela version` | Print the tool version. | |
| `vela help [<command>]` | Help for all commands or one command. | `--json` (the whole command table) |

`-p` means `--package` for `plan`, `apply`, `inspect` and `models`, and `--project` for `add`.

## Plan and apply options

| Option | Notes |
| --- | --- |
| `--solution <uniquename>` | Alias `--solution-unique-name`. The unmanaged solution the run deploys into. Required by `plan` and `apply` (prompted for with `--interactive`). |
| `--secure-config NAME=VALUE` or `NAME=@file` | Value of a step's named secure configuration. Repeatable. An inline value warns; prefer `@file` or `VELA_SECURE_CONFIG_<NAME>`. |
| `--managed-identity <managedidentityid>` | Link the published plugin packages to this `managedidentity` record. |
| `--strict` | Exit `4` when `diagnostics` has any warning. |
| `--detailed-exitcode` | `plan` only: exit `8` when there are changes to apply. |
| `--auto-approve` / `--yes` | `apply` only: skip the confirmation prompt. Required when stdin is not a terminal. |
| `--batch-size <n>` | `apply` only: requests per `ExecuteMultiple` (1 to 1000, default 100). |
| `--environment <label>` | Label shown in the output. Defaults to `--url`. |
| `--json` / `--out <file>` | JSON on stdout / JSON written to a file. |

## Package inputs

Used by `plan`, `apply` and `inspect` (and by `models` with a bare `--custom-apis`).

| Option | Notes |
| --- | --- |
| `--package <file.nupkg>` / `-p` | A built plugin package. Repeatable. |
| `--packages-dir <folder>` | Every `.nupkg` in the folder, keeping the highest version of each package id. |
| `--package-assembly <name.dll>` | The plugin assembly inside the package, when it cannot be inferred. |

## Connection options

Shared by `plan`, `apply`, `inspect`, `models`, `logs` and `solution show`. See
[Authentication](../configuration/authentication.md).

| Option | Notes |
| --- | --- |
| `--url <https://org.crm.dynamics.com>` | Environment URL. On its own, uses your ambient Azure credential (`az login`, managed identity). |
| `--tenant-id`, `--client-id` | App registration for client-secret authentication. |
| `--client-secret-stdin` | Read the client secret from piped stdin. |
| `--client-secret <secret>` | Works but warns: visible in shell history and process listings. Prefer `DATAVERSE_CLIENT_SECRET` or `--client-secret-stdin`. |
| `--connection-string "<...>"` | A full Dataverse connection string. |
| `--interactive` / `-i` | Browser sign-in. The token is cached under `~/.vela`, so later runs are silent. |
| `--fresh-login` | Ignore the cached account and sign in again. |
| `--auth-client-id`, `--authority` | Override the client id and authority of the browser sign-in (sovereign clouds, custom app registrations). |
| `--timeout <minutes>` | Dataverse request timeout (1 to 60, default 10). |

## Environment variables

| Variable | Effect |
| --- | --- |
| `DATAVERSE_CLIENT_SECRET` | Client secret for app-registration authentication (instead of `--client-secret`). |
| `DATAVERSE_CONNECTION_STRING` | Connection string (instead of `--connection-string`). |
| `VELA_SECURE_CONFIG_<NAME>` | Value of the step secure configuration `<NAME>` (instead of `--secure-config`). |
| `VELA_CONFIG_DIR` | Folder of the token cache and the remembered URL (default `~/.vela`). |
| `NO_COLOR` / `VELA_NO_COLOR` | Turn off colors. |
| `VELA_NO_UPDATE_CHECK` | Turn off the daily update notice. |
| `VELA_SCAN_IN_PROCESS=1` | Scan packages inside the `vela` process instead of a child process. For debugging only; see [Plan and apply](../guides/plan-apply.md#package-code-runs-in-a-separate-process). |
| `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY` | Proxy settings. See [Behind a proxy](../guides/cli-overview.md#behind-a-proxy). |

## Models scopes

`vela models` needs at least one scope; it never generates everything implicitly. Scopes combine.

| Scope or option | Generates or does |
| --- | --- |
| `--solution <uniquename>` | Early-bound classes for the tables of the solution. |
| `--entities a,b,c` | Early-bound classes for the named tables. |
| `--custom-apis [a,b,c]` | Typed request and response classes. With no value: every Custom API in the environment, with a warning. |
| `--messages [a,b,c]` | Typed request and response classes for SDK requests, by request name (for example `SendEmail`). With no value: every request of the consumable messages. |
| `--roles "A,B"` | A dependency-light `Roles` class. Missing names are skipped with a warning. |
| `--solution-roles` | Every security role in `--solution`. |
| `--intersect` | Also the N:N intersect tables of the scoped tables. |
| `--from-solution <zip>` | Offline: entity models and option sets from an exported solution zip. |
| `--forms` / `--tables` | TypeScript form models / Web API record interfaces *(preview)*. |
| `--verify` | Write nothing: compare the entity model on disk (and the `Roles` class with `--roles` or `--solution-roles`) with the environment. Exit `5` on drift. |
| `--lang csharp\|ts` | Output language (C# by default). |
| `--namespace`, `--project`, `--out`, `--no-annotations`, `--overwrite` | Shape the output. |
| `--package` / `-p`, `--packages-dir`, `--package-assembly` | With a bare `--custom-apis`: leave out the typed Custom APIs your package already defines. |
| `--json`, `--verbose` / `-v` | Machine-readable report / more detail on stderr. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `2` | Invalid arguments. |
| `3` | File or folder not found. |
| `4` | Error: a `--strict` failure, a refused unattended `apply`, a missing or managed `--solution`, or any other failure. |
| `5` | Drift detected (`vela models --verify`, or an `apply` that did not converge). |
| `6` | Dataverse connection failure. |
| `7` | Partial apply: re-run `apply` to finish. |
| `8` | Changes pending (`vela plan --detailed-exitcode` only). |

This is a summary. [Machine-readable output](machine-readable-output.md#exit-codes) is the reference for every case
behind each code.
