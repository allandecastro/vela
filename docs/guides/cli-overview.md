# The vela CLI

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command. See
[Install](../getting-started/install.md) to get it. Then run, for example:

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

`vela help` lists the commands; `vela help <command>` (or `vela <command> --help`) prints that command's usage,
options, notes and examples, and `vela help --json` describes every command, option and exit code for scripts and
coding agents. The help is generated from the same table that validates the options, so it never lists an option
the command rejects.

Core commands:

| Command | What it does |
| --- | --- |
| `vela init` / `vela new` | [Scaffold](scaffold.md) a complete, build-ready plugin solution. |
| `vela add` | [Generate a single file](add.md) (plugin / service / customapi / test) into an existing project, or add a step to an existing plugin. |
| `vela models` | [Generate early-bound classes](models.md) for a solution or table list over your connection. |
| `vela plan` | Preview the create/update/delete operations needed to converge Dataverse with your package (read-only). |
| `vela apply` | Apply those operations (idempotent upsert/delete); writes by default, prompting on a terminal or `--auto-approve` for CI. See [CLI automation](plan-apply.md). |
| `vela inspect` | Report local package registrations alongside live Dataverse state, with diagnostics (read-only). |
| `vela solution show <uniquename>` | Print the publisher prefix of a solution (read-only). See [below](#solution-prefix-vela-solution-show). |
| `vela logs` (alias `vela trace`) | Read the Plug-in Trace Log, or failed system jobs with `--jobs` (read-only). See [Reading execution logs](#reading-execution-logs-vela-logs). |
| `vela sign` | [Sign a plugin package](signing.md) (required for managed-identity plugins). |
| `vela logout` | Clear the cached interactive token and remembered Dataverse URL. |
| `vela update` | Update the vela global tool to the latest published version (or `--version <x.y.z>`). |
| `vela menu` (or bare `vela`) | Launch the interactive menu that guides you through the commands above. |
| `vela version` | Print the tool version. |
| `vela help [<command>]` | Help for one command, or `--json` for the whole command table. |

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
| `4` | Error: a malformed package, definition or response, a `--strict` failure, a refused non-interactive `apply` without `--auto-approve`, or an unexpected failure. |
| `5` | Drift detected (`vela models --verify` found the local model out of sync with the live schema, or `apply` wrote every change but the environment still differs afterwards). |
| `6` | Dataverse connection failure: auth, endpoint, or a network error (DNS, socket, HTTP, timeout) on any call, including while reading the live state. |
| `7` | Partial apply (a mid-run `apply` failure; Dataverse may be partially updated, so re-run `apply` to reconcile). |
| `8` | Changes pending (`vela plan --detailed-exitcode` only). |

> [!NOTE]
> **Client-side options (preview).** `vela models --forms` / `--tables`, `vela init --web-resources` and
> `vela plan` / `apply --webresources` belong to a client-side preview that may change.

> [!NOTE]
> **Output: human on a terminal, JSON when piped.** `plan`, `apply`, and `inspect` print a colorized,
> human-readable summary when stdout is a terminal, and machine-readable JSON when the output is piped,
> redirected, or written with `--out <file>`. Pass `--json` to force JSON even on a terminal. Color is
> disabled automatically for non-terminal output and can be turned off with the `NO_COLOR` or `VELA_NO_COLOR`
> environment variables. Scripts and coding agents that consume the JSON are unaffected. `init`, `add`, `models`,
> `logs`, `solution show` and `sign` also take `--json`.

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
> **Option syntax.** Every option accepts both `--option value` and `--option=value`. Each command accepts only
> its own options: a misspelled option, or one that belongs to another command (`plan --auto-approve`), is
> rejected up front with exit `2`, and an option that needs a value but has none (it is the last token, or is followed by
> another known option) fails with `Option '--x' requires a value.` rather than silently doing the wrong thing.

> [!NOTE]
> **Update notice.** After a command runs, the CLI checks nuget.org at most once a day and prints a one-line
> notice to stderr when a newer version is available (`dotnet tool update -g Vela.Cli`). It never blocks or fails a
> command, and is silent for `--json` output. Set `VELA_NO_UPDATE_CHECK` to disable it.

## Behind a proxy

`vela` runs on .NET 10, so it takes its proxy the way .NET does
([`HttpClient.DefaultProxy`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.defaultproxy)), not
from a `.config` file: the `<system.net><defaultProxy>` element of an `app.config` is a .NET Framework setting, and
`vela` ignores it.

- `HTTPS_PROXY` (or `ALL_PROXY`) names the proxy; `NO_PROXY` lists the hosts reached directly, comma-separated,
  with a leading dot for subdomains (`.contoso.com`).
- When none of them is set, Windows and macOS use the system proxy settings; Linux connects directly.

```bash
export HTTPS_PROXY=http://proxy.contoso.com:8080      # PowerShell: $env:HTTPS_PROXY = "http://proxy.contoso.com:8080"
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

The connection to Dataverse and the sign-in requests go through that proxy. With `--interactive`, the sign-in page
itself opens in your browser, which uses its own proxy settings. A proxy that can't be reached fails the command
with exit code 6, like any connection failure. The proxy URL may carry credentials
(`http://user:password@proxy:8080`), which leaves the password in the environment of every process started from
that shell.

`dotnet tool install` / `vela update` download the tool through NuGet, which has its own
[proxy settings](https://learn.microsoft.com/nuget/reference/nuget-config-file#config-section). The generated
ConsoleApp is .NET 10 too and follows the same rules. Plugin code runs in the Dataverse sandbox, where your proxy
settings play no part.

## Solution prefix (`vela solution show`)

`vela solution show <uniquename>` connects with the usual options and prints the customization prefix of the
solution's publisher, so you can align a scaffold with the solution before the first `apply` rejects a mismatch:

```bash
vela solution show ZavaSales -i            # prints: zava
vela solution show ZavaSales -i --json     # {"solution":"ZavaSales","publisherPrefix":"zava"}
vela init --name ZavaSales --prefix Zava --publisher-prefix "$(vela solution show ZavaSales -i)" --solution ZavaSales
```

It is read-only. `show` is its only subcommand.

## Reading execution logs (`vela logs`)

`vela logs` (alias `vela trace`) is a **read-only** reader of a Dataverse environment's execution diagnostics, so
you - or a coding agent - can see why a plugin failed without opening the UI. It reads two sources:

- **Plug-in Trace Log** (default): the trace output of plugins (what `ctx.Trace(...)` and the automatic
  handler-entry line emit). Only populated when plug-in trace logging is enabled on the environment
  ([Enable trace logging](https://learn.microsoft.com/power-apps/developer/data-platform/logging-tracing#enable-trace-logging)). On an empty result, `vela logs` says so and points at that setting.
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
`--message <sdk message>`, `--entity <logical name>`, `--correlation <guid>`, `--errors` (trace log only: rows with a non-empty exception),
`--full` (do not truncate the trace/exception text). `--json` emits `{ mode, count, logs: [...] }`; the trace text
and exception are always complete in JSON. Connects with the usual options (`-i` or `--connection-string` /
client-secret flags); it never writes or deletes anything.
