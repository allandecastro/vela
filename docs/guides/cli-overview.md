# The vela CLI

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command. See
[Install](../getting-started/install.md) to get it. Then run, for example:

```powershell
vela plan --packages-dir .\artifacts --interactive
```

Core commands:

| Command | What it does |
| --- | --- |
| `vela init` / `vela new` | [Scaffold](scaffold.md) a complete, build-ready plugin solution. |
| `vela add` | [Generate a single file](add.md) (plugin / service / customapi / test) into an existing project, or add a step to one. |
| `vela models` | [Generate early-bound classes](models.md) for a solution or table list over your connection. |
| `vela plan` | Preview the create/update/delete operations needed to converge Dataverse with your package (read-only). |
| `vela apply` | Apply those operations (idempotent upsert/delete); writes by default, prompting on a terminal or `--auto-approve` for CI. See [CLI automation](plan-apply.md). |
| `vela inspect` | Report local package registrations alongside live Dataverse state, with diagnostics (read-only). |
| `vela logs` (alias `vela trace`) | Read the Plug-in Trace Log, or failed system jobs with `--jobs` (read-only). See [Reading execution logs](#reading-execution-logs-vela-logs). |
| `vela sign` | [Sign a plugin package](signing.md) (required for managed-identity plugins). |
| `vela logout` | Clear the cached interactive token and remembered Dataverse URL. |
| `vela update` | Update the vela global tool to the latest published version (or `--version <x.y.z>`). |
| `vela menu` (or bare `vela`) | Launch the interactive menu that guides you through the commands above. |
| `vela version` | Print the tool version. |

> [!NOTE]
> **`vela update`** runs `dotnet tool update --global Vela.Cli` for you. On **Windows** the running executable is
> locked, so it cannot replace itself while `vela` is running - the command detects this and asks you to run
> `dotnet tool update -g Vela.Cli` from a fresh shell. On macOS/Linux it updates in place.

### Exit codes

Every command returns a stable exit code, so scripts and coding agents can branch on the outcome:

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `2` | Invalid arguments (unknown option, missing/insufficient scope). |
| `3` | A required file was not found (for example a package path). |
| `4` | Parse/read or other error (a malformed package, definition or response, or an unexpected failure). |
| `5` | Drift detected (`vela models --verify` found the local model out of sync with the live schema). |
| `6` | Dataverse connection failure (auth or endpoint). |
| `7` | Partial apply (a mid-run `apply` failure; Dataverse may be partially updated, so re-run `apply` to reconcile). |

> [!NOTE]
> **Client-side (preview / work in progress).** For the **vela-client** (separate) runtime,
> `vela models --forms` generates [TypeScript FormModel files](models.md) and
> `vela models --tables` [TypeScript Web API record interfaces](models.md),
> `vela init --web-resources` [scaffolds a web resources project](scaffold.md), and `vela plan` /
> `apply --webresources` [deploy web resources](plan-apply.md). These are new and may change. Most commands also
> take `--json` for machine-readable output.

> [!NOTE]
> **Output: human on a terminal, JSON when piped.** `plan`, `apply`, and `inspect` print a colorized,
> human-readable summary when stdout is a terminal, and machine-readable JSON when the output is piped,
> redirected, or written with `--out <file>`. Pass `--json` to force JSON even on a terminal. Color is
> disabled automatically for non-terminal output and can be turned off with the `NO_COLOR` or `VELA_NO_COLOR`
> environment variables. Scripts and coding agents that consume the JSON are unaffected.

> [!TIP]
> **Secrets:** prefer the `DATAVERSE_CLIENT_SECRET` environment variable or `--client-secret-stdin`
> over `--client-secret`, which is exposed to shell history and process listings (the CLI warns when
> you use it). For sovereign clouds / custom app registrations under `--interactive`, override the
> MSAL login with `--auth-client-id` and `--authority`. Add `--verbose` for full exception stack traces
> on failure.

> [!TIP]
> **Interactive login is remembered.** The first `--interactive` run opens a browser; the token is then
> cached (`~/.vela/token.dat`, DPAPI-encrypted on Windows) so later runs are silent, and the last Dataverse
> URL is offered as the prompt default. Use `--fresh-login` to force a new prompt, or `vela logout` to clear
> the cached token and remembered URL. Override the config location with `$VELA_CONFIG_DIR`.

> [!TIP]
> **Option syntax.** Every option accepts both `--option value` and `--option=value`. A misspelled option is
> rejected up front, and an option that needs a value but has none (it is the last token, or is followed by
> another known option) fails with `Option '--x' requires a value.` rather than silently doing the wrong thing.

> [!NOTE]
> **Update notice.** After a command runs, the CLI checks nuget.org at most once a day and prints a one-line
> notice to stderr when a newer version is available (`dotnet tool update -g Vela.Cli`). It never blocks or fails a
> command, and is silent for `--json` output. Set `VELA_NO_UPDATE_CHECK` to disable it.

## Reading execution logs (`vela logs`)

`vela logs` (alias `vela trace`) is a **read-only** reader of a Dataverse environment's execution diagnostics, so
you - or a coding agent - can see why a plugin failed without opening the UI. It reads two sources:

- **Plug-in Trace Log** (default): the trace output of plugins (what `ctx.Trace(...)` and the automatic
  handler-entry line emit). Only populated when plug-in tracing is enabled on the org (System Settings >
  Customization > *Enable logging to plug-in trace log*: `All` or `Exception`). On an empty result, `vela logs`
  says so and points at that setting.
- **Failed system jobs** with `--jobs`: the `asyncoperation` rows, where an **asynchronous** step's failure is
  recorded (with its error message) **even when trace logging is off**. Defaults to failed jobs; `--all-statuses`
  shows every status.

```bash
# recent plugin errors in the last hour (interactive login)
vela logs --errors --since 1h -i

# why did my async step fail? (trace log may be off - look at the jobs)
vela logs --jobs --since 1d -i

# machine-readable, for an agent to parse
vela logs --since 2h --type AccountNamePlugin --json
```

Filters: `--top <n>` (1-200, default 20, newest first), `--since 30m|2h|3d|<date>`, `--type <plugin type>`,
`--message <sdk message>`, `--entity <logical name>`, `--correlation <guid>`, `--errors` (trace log only),
`--full` (do not truncate the trace/exception text). `--json` emits `{ mode, count, logs: [...] }`; the trace text
and exception are always complete in JSON. Connects with the usual options (`-i` or `--connection-string` /
client-secret flags); it never writes or deletes anything.
