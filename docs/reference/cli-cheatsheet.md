# CLI cheat sheet

A one-page reference for the `vela` CLI: commands, the shared connection/output flags, the `models` scopes, and
exit codes. For the full narrative, see [The vela CLI](../guides/cli-overview.md).

## Commands

| Command | Purpose | Key options |
| --- | --- | --- |
| `vela init` (alias `new`) | Scaffold a build-ready solution (minimal by default) | `--name`, `--prefix`, `--publisher-prefix`, `--samples` (demo code + Integrations/ConsoleApp), `--output`, `--no-tests` (with `--samples`: `--no-console`/`--no-integrations`, `--managed-identity`), `--web-resources`, `--core-version`, `--force` (keeps your and edited files), `--verbose`, `--json` |
| `vela add <kind> <Name>` | Generate one file into a project | kinds: `plugin`/`service`/`customapi`/`test`/`step`/`form-script`/`ribbon-script`; `--entity`, `--message`, `--stage`, `--mode`, `--filtering-attributes` (required for Create/Update steps), `--handler` (step), `--function`/`--action`, `--bound`/`--global`, `--prefix` (customapi/test publisher prefix; default: the project's `VelaPublisherPrefix`), `--project`, `--force` (not step), `--json`; an option a kind does not use is rejected |
| `vela models <scope>` | Generate early-bound / typed models | see [scopes](#models-scopes) + connection flags |
| `vela plan` | Preview registrations (read-only) | package inputs + connection flags, `--solution`, `--secure-config NAME=VALUE\|NAME=@file`, `--strict`, `--detailed-exitcode`, `--verbose`, `--json`, `--out` |
| `vela apply` | Apply registrations (writes) | package inputs + connection flags, `--solution`, `--secure-config`, `--auto-approve` (alias `--yes`), `--batch-size`, `--strict`, `--webresources`, `--json`, `--out` |
| `vela inspect` | Compare local packages with the live environment | package inputs + connection flags, `--secure-config`, `--json`, `--out` |
| `vela solution show <uniquename>` | Print a solution's publisher prefix | connection flags, `--json` |
| `vela logs` (alias `trace`) | Read the plug-in trace log, or failed system jobs (read-only) | `--jobs` (`--all-statuses`), `--errors`, `--top`, `--since`, `--type`, `--message`, `--entity`, `--correlation`, `--full`, `--json` + connection flags |
| `vela help [<command>]` | Help for one command; `vela help --json` for the whole command table | |
| `vela sign` | Sign a plugin package | `--package`, `--fingerprint` \| (`--pfx`/`--pfx-base64-env <VAR>` + `--pfx-password-env <VAR>`/`--pfx-credential`; literal `--pfx-base64`/`--pfx-password` warn), `--timestamp` |
| `vela logout` | Clear the cached interactive token and remembered URL | |
| `vela update` | Update the global tool (`dotnet tool update --global Vela.Cli`) | `--version <x.y.z>` (Windows: run from a fresh shell, the running exe is locked) |
| `vela menu` (or bare `vela`) | Interactive menu | |
| `vela version` | Print the tool version | |

Package inputs (for `plan`/`apply`/`inspect`): `--package <file.nupkg>` (repeatable), `--packages-dir <folder>`
(the newest version of each package id), `--package-assembly <name>`. Each command accepts only its own options
(`vela <command> --help` lists them), so `plan --auto-approve` is an error.

## Connection & auth (shared by `plan` / `apply` / `inspect` / `models`)

| Option | Notes |
| --- | --- |
| `--url <https://org.crm.dynamics.com>` | Environment URL |
| `--tenant-id` / `--client-id` / `--client-secret` | Client-secret auth |
| `--client-secret-stdin` / `DATAVERSE_CLIENT_SECRET` | Preferred over `--client-secret` (which leaks to shell history) |
| `--connection-string "<...>"` | Full Dataverse connection string |
| `--interactive` | Browser (MSAL) login; token cached under `~/.vela`. Safe under redirected stdin (never blocks on a prompt) |
| `--fresh-login` | Force a new interactive prompt |
| `--auth-client-id` / `--authority` | Override the MSAL client / authority (sovereign clouds) |
| `--timeout <minutes>` | Dataverse request timeout (default 10, range 1 to 60); raise for a large org or slow cold start |
| `--environment <label>` | Display label for the run (falls back to `--url`) |

## Models scopes

`vela models` needs at least one scope (it refuses to generate everything implicitly). Scopes combine.

| Scope | Generates |
| --- | --- |
| `--solution <uniquename>` | Early-bound classes for the solution's tables |
| `--entities a,b,c` | Early-bound classes for the named tables |
| `--custom-apis [a,b,c]` | Typed request/response classes (no value = every Custom API in the environment, with a warning) |
| `--messages [a,b,c]` | Typed request/response classes for SDK messages (no value = all consumable messages) |
| `--roles "A,B"` | A dependency-light `Roles` class (missing names are skipped with a warning) |
| `--solution-roles` | Every security role in `--solution` |
| `--forms` / `--tables` | TypeScript FormModel files / Web API record interfaces (preview) |
| `--verify` | Do not write: compare the generated entity model on disk against the live schema and report drift (exit `5` on drift) |
| `--lang csharp\|ts` | Output language (C# default; forms/tables are TypeScript) |
| `--namespace`, `--out`, `--no-annotations`, `--overwrite` | Output shaping |

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
| `5` | Drift detected (`vela models --verify` found the local model out of sync) |
| `6` | Dataverse connection failure |
| `7` | Partial apply (mid-run failure; re-run `apply` to reconcile) |
| `8` | Changes pending (`vela plan --detailed-exitcode` only) |
