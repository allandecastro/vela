# CLI cheat sheet

A one-page reference for the `vela` CLI: commands, the shared connection/output flags, the `models` scopes, and
exit codes. For the full narrative, see [The vela CLI](../guides/cli-overview.md).

## Commands

| Command | Purpose | Key options |
| --- | --- | --- |
| `vela init` (alias `new`) | Scaffold a build-ready solution (minimal by default) | `--name`, `--prefix`, `--publisher-prefix`, `--solution` (both recorded in `PROJECT.md`, which the CLI never reads), `--samples` (demo code + Integrations/ConsoleApp; with it, `--no-console`/`--no-integrations`), `--output`, `--no-tests`, `--integrations` (empty Integrations project), `--managed-identity` (implies `--integrations`), `--core-version`, `--force` (keeps your and edited files), `--interactive`, `--verbose`, `--json` |
| `vela add <kind> <Name>` | Generate one file into a project | kinds: `plugin`/`service`/`customapi`/`test`/`step`/`form-script`/`ribbon-script`; a `plugin`'s `<Name>` is optional (default `<Entity>Plugin`); `--entity` (optional for `service`: without it, a service shared by several tables), `--message`, `--stage`, `--mode`, `--filtering-attributes` (required for Create/Update steps), `--handler` (plugin/step; default `Handle<Message>`), `--function`/`--action`, `--bound`/`--global`, `--prefix` (publisher prefix: required for `customapi`, and for `test --kind customapi` when the unique name cannot be read), `--project`, `--force` (not step), `--json`; an option a kind does not use is rejected |
| `vela models <scope>` | Generate early-bound / typed models | see [scopes](#models-scopes) + connection flags |
| `vela plan` | Preview registrations (read-only) | package inputs + connection flags, `--solution` (required; one solution per run), `--secure-config NAME=VALUE\|NAME=@file`, `--managed-identity <managedidentityid>`, `--strict`, `--detailed-exitcode`, `--environment`, `--verbose`, `--json`, `--out` |
| `vela apply` | Apply registrations (writes) | package inputs + connection flags, `--solution` (required; one solution per run), `--secure-config`, `--managed-identity <managedidentityid>`, `--auto-approve` (alias `--yes`), `--batch-size`, `--strict`, `--environment`, `--verbose`, `--json`, `--out` |
| `vela inspect` | Compare local packages with the live environment (read-only) | package inputs + connection flags, `--solution` (optional), `--secure-config`, `--environment`, `--verbose`, `--json`, `--out` |
| `vela solution show <uniquename>` | Print a solution's publisher prefix | connection flags, `--json` |
| `vela logs` (alias `trace`) | Read the plug-in trace log, or failed system jobs (read-only) | `--jobs` (`--all-statuses`), `--errors`, `--top`, `--since`, `--type`, `--message`, `--entity`, `--correlation`, `--full`, `--json` + connection flags |
| `vela help [<command>]` | Help for one command; `vela help --json` for the whole command table | |
| `vela sign` | Sign a plugin package | `--package`, `--fingerprint` \| (`--pfx`/`--pfx-base64-env <VAR>` + `--pfx-password-env <VAR>`/`--pfx-credential`; literal `--pfx-base64`/`--pfx-password` warn), `--timestamp`, `--json` |
| `vela logout` | Clear the cached interactive token and remembered URL | |
| `vela update` | Update the global tool (`dotnet tool update --global Vela.Cli`) | `--version <x.y.z>` (Windows: run from a fresh shell, the running exe is locked) |
| `vela menu` (or bare `vela`) | Interactive menu | |
| `vela version` | Print the tool version | |

Package inputs (for `plan`/`apply`/`inspect`): `--package <file.nupkg>` (repeatable), `--packages-dir <folder>`
(the newest version of each package id), `--package-assembly <name>`. Each command accepts only its own options
(`vela <command> --help` lists them), so `plan --auto-approve` is an error.

## Connection & auth (shared by `plan` / `apply` / `inspect` / `models` / `logs` / `solution show`)

| Option | Notes |
| --- | --- |
| `--url <https://org.crm.dynamics.com>` | Environment URL; alone, uses your ambient Azure credential (`az login`, managed identity) |
| `--tenant-id` / `--client-id` / `--client-secret` | Client-secret auth |
| `--client-secret-stdin` / `DATAVERSE_CLIENT_SECRET` | Preferred over `--client-secret` (which leaks to shell history) |
| `--connection-string "<...>"` | Full Dataverse connection string |
| `--interactive` / `-i` | Browser (MSAL) login; token cached under `~/.vela`. Safe under redirected stdin (never blocks on a prompt) |
| `--fresh-login` | Force a new interactive prompt |
| `--auth-client-id` / `--authority` | Override the MSAL client / authority (sovereign clouds) |
| `--timeout <minutes>` | Dataverse connection timeout (default 10, range 1 to 60); raise for a large org or slow cold start |

## Environment variables

| Variable | Effect |
| --- | --- |
| `DATAVERSE_CLIENT_SECRET` | Client secret for app-registration auth (instead of `--client-secret`) |
| `DATAVERSE_CONNECTION_STRING` | Connection string (instead of `--connection-string`) |
| `VELA_SECURE_CONFIG_<NAME>` | Value of the step secure configuration `<NAME>` (instead of `--secure-config`) |
| `VELA_CONFIG_DIR` | Location of the token cache and remembered URL (default `~/.vela`) |
| `NO_COLOR` / `VELA_NO_COLOR` | Disable colors |
| `VELA_NO_UPDATE_CHECK` | Disable the daily update notice |
| `VELA_SCAN_IN_PROCESS=1` | Scan packages in the `vela` process instead of a child process (debugging only) |

## Models scopes

`vela models` needs at least one scope (it refuses to generate everything implicitly). Scopes combine.

| Scope | Generates |
| --- | --- |
| `--solution <uniquename>` | Early-bound classes for the solution's tables |
| `--entities a,b,c` | Early-bound classes for the named tables |
| `--custom-apis [a,b,c]` | Typed request/response classes (no value = every Custom API in the environment, with a warning) |
| `--messages [a,b,c]` | Typed request/response classes for SDK requests, by request name (e.g. `SendEmail`; no value = every request of the consumable messages) |
| `--roles "A,B"` | A dependency-light `Roles` class (missing names are skipped with a warning) |
| `--solution-roles` | Every security role in `--solution` |
| `--intersect` | Also the N:N intersect tables of the scoped tables |
| `--from-solution <zip>` | Offline: entity models and option sets from an exported solution zip |
| `--forms` / `--tables` | TypeScript FormModel files / Web API record interfaces (preview) |
| `--verify` | Do not write: compare the generated entity model (and, with `--roles` / `--solution-roles`, the `Roles` class) on disk against the environment and report drift (exit `5` on drift) |
| `--lang csharp\|ts` | Output language (C# default; forms/tables are TypeScript) |
| `--namespace`, `--project`, `--out`, `--no-annotations`, `--overwrite`, `--json` | Output shaping |
| `--package` / `--packages-dir` / `--package-assembly` | With a bare `--custom-apis`: exclude the typed Custom APIs your package already defines |

## Output & exit codes

`plan`/`apply`/`inspect` print a colorized human summary on a terminal and a versioned JSON document when
piped/redirected (force with `--json` or `--out <file>`); notices and `--verbose` detail go to stderr. The fields are
documented in [Machine-readable output](machine-readable-output.md). `NO_COLOR`/`VELA_NO_COLOR` disable color.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `2` | Invalid arguments |
| `3` | File not found |
| `4` | Error, including a `--strict` failure |
| `5` | Drift detected (`vela models --verify` found the local model out of sync, or `apply` did not converge) |
| `6` | Dataverse connection failure |
| `7` | Partial apply (mid-run failure; re-run `apply` to reconcile) |
| `8` | Changes pending (`vela plan --detailed-exitcode` only) |
