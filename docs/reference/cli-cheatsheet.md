# CLI cheat sheet

A one-page reference for the `vela` CLI: commands, the shared connection/output flags, the `models` scopes, and
exit codes. For the full narrative, see [The vela CLI](../guides/cli-overview.md).

## Commands

| Command | Purpose | Key options |
| --- | --- | --- |
| `vela init` (alias `new`) | Scaffold a build-ready solution | `--name`, `--prefix`, `--output`, `--no-tests`/`--no-console`/`--no-integrations`, `--managed-identity`, `--web-resources`, `--core-version`, `--force` |
| `vela add <kind> <Name>` | Generate one file into a project | kinds: `plugin`/`service`/`customapi`/`test`/`step`/`form-script`/`ribbon-script`; `--entity`, `--message`, `--filtering-attributes`, `--function`/`--action`, `--bound`/`--global`, `--project`, `--force` |
| `vela models <scope>` | Generate early-bound / typed models | see [scopes](#models-scopes) + connection flags |
| `vela plan` | Preview registrations (read-only) | package inputs + connection flags, `--strict`, `--verbose`, `--json`, `--out` |
| `vela apply` | Apply registrations (writes) | same as `plan` + `--solution`, `--auto-approve` (alias `--yes`), `--webresources` |
| `vela inspect` | Compare local packages with the live environment | package inputs + connection flags |
| `vela sign` | Sign a plugin package | `--package`, `--fingerprint` \| (`--pfx`/`--pfx-base64` + `--pfx-password`/`--pfx-credential`), `--timestamp` |
| `vela logout` | Clear the cached interactive token and remembered URL | |
| `vela menu` (or bare `vela`) | Interactive menu | |
| `vela version` | Print the tool version | |

Package inputs (for `plan`/`apply`/`inspect`): `--package <file.nupkg>` (repeatable), `--packages-dir <folder>`,
`--package-assembly <name>`.

## Connection & auth (shared by `plan` / `apply` / `inspect` / `models`)

| Option | Notes |
| --- | --- |
| `--url <https://org.crm.dynamics.com>` | Environment URL |
| `--tenant-id` / `--client-id` / `--client-secret` | Client-secret auth |
| `--client-secret-stdin` / `DATAVERSE_CLIENT_SECRET` | Preferred over `--client-secret` (which leaks to shell history) |
| `--connection-string "<...>"` | Full Dataverse connection string |
| `--interactive` | Browser (MSAL) login; token cached at `~/.vela/token.dat`. Safe under redirected stdin (never blocks on a prompt) |
| `--fresh-login` | Force a new interactive prompt |
| `--auth-client-id` / `--authority` | Override the MSAL client / authority (sovereign clouds) |
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
| `--lang csharp\|ts` | Output language (C# default; forms/tables are TypeScript) |
| `--namespace`, `--out`, `--no-annotations`, `--overwrite` | Output shaping |

## Output & exit codes

`plan`/`apply`/`inspect` print a colorized human summary on a terminal and JSON when piped/redirected (force with
`--json` or `--out <file>`). `NO_COLOR`/`VELA_NO_COLOR` disable color.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `2` | Invalid arguments |
| `3` | File not found |
| `4` | Parse/read or other error |
| `6` | Dataverse connection failure |
