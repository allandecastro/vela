# The vela CLI

`Vela.Cli` is a .NET global tool that installs the **`vela`** command. This page lists the commands, explains the
conventions they share (help, option syntax, output, exit codes, update notices, proxies), and documents the two
small commands that have no page of their own: `vela solution show` and `vela logs`.

To install the tool, see [Install](../getting-started/install.md). For a one-page list of every option, see the
[CLI cheat sheet](../reference/cli-cheatsheet.md).

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

## Commands

| Command | What it does | Details |
| --- | --- | --- |
| `vela init` (alias `new`) | Scaffold a build-ready plugin solution. | [Scaffold a solution](scaffold.md) |
| `vela add` | Add a plugin, service, Custom API, test or step to an existing project. | [Add a file](add.md) |
| `vela models` | Generate early-bound classes, typed messages or a `Roles` class. | [Early-bound models](models.md) |
| `vela plan` | Preview the changes needed to bring Dataverse in line with your packages. Read-only. | [Plan and apply](plan-apply.md) |
| `vela apply` | Make those changes. Asks for confirmation on a terminal; needs `--auto-approve` in a script. | [Plan and apply](plan-apply.md) |
| `vela inspect` | Show the registrations in your packages next to the live ones. Read-only. | [Plan and apply](plan-apply.md) |
| `vela solution show <uniquename>` | Print the publisher prefix of a solution. Read-only. | [Below](#print-a-solutions-publisher-prefix-vela-solution-show) |
| `vela logs` (alias `trace`) | Read the Plug-in Trace Log, or failed system jobs with `--jobs`. Read-only. | [Below](#reading-execution-logs-vela-logs) |
| `vela sign` | Sign a plugin package (needed for managed-identity plugins). | [Signing](signing.md) |
| `vela logout` | Remove the cached interactive-login token and the remembered Dataverse URL. | [Authentication](../configuration/authentication.md#interactive-browser-login) |
| `vela update` | Update the tool to the latest version on nuget.org, or to `--version <x.y.z>`. | [Below](#update-the-tool) |
| `vela menu` (or `vela` with no arguments) | Open an interactive menu that guides you through the commands. | |
| `vela version` | Print the tool version. | |
| `vela help [<command>]` | Print help for all commands or one command; `--json` for the whole command table. | [Below](#get-help) |

The commands that connect to Dataverse (`plan`, `apply`, `inspect`, `models`, `logs` and `solution show`) share the
same connection options. See [Authentication](../configuration/authentication.md).

## Get help

- `vela help` lists the commands.
- `vela help <command>` (or `vela <command> --help`) prints the usage, options, notes and examples of one command.
- `vela help --json` describes every command, option and exit code as JSON, for scripts and coding agents. See
  [The command table](../reference/machine-readable-output.md#the-command-table).

The help is generated from the same table that validates the options, so it lists exactly the options each command
accepts.

## Option syntax

- Every option accepts both `--option value` and `--option=value`.
- Each command accepts only its own options. A misspelled option, or one that belongs to another command (such as
  `plan --auto-approve`), fails with exit code `2` before anything runs.
- An option that needs a value but has none (it is the last token, or is followed by another option) fails with
  `Option '--x' requires a value.`
- Add `--verbose` (`-v`) to the commands that support it for more detail on stderr and the full stack trace on
  failure.

## Output and exit codes

`plan`, `apply` and `inspect` print a colored summary when stdout is a terminal, and a versioned JSON document when
stdout is piped or redirected, with `--json`, or with `--out <file>`. `init`, `add`, `models`, `logs`,
`solution show` and `sign` also accept `--json`. Notices, warnings and `--verbose` detail always go to stderr.

Color is turned off for output that is not a terminal, and when the `NO_COLOR` or `VELA_NO_COLOR` environment
variable is set.

Every command returns a stable exit code: `0` on success, `2` for invalid arguments, `4` for an error, `6` for a
connection failure, and a few more for specific outcomes. [Machine-readable output](../reference/machine-readable-output.md#exit-codes) lists every exit code and the cases
behind it, and documents the JSON fields.

## Update the tool

`vela update` runs `dotnet tool update --global Vela.Cli` for you. On Windows the running executable is locked, so
when the update cannot replace it, `vela update` asks you to run `dotnet tool update --global Vela.Cli` from a new
shell.

After a command runs, the CLI checks nuget.org at most once a day and, when a newer version exists, prints a one-line
notice to stderr. The check never blocks or fails a command and is skipped for `--json` output. Set
`VELA_NO_UPDATE_CHECK` to turn it off.

## Behind a proxy

`vela` runs on .NET 10, so it picks up a proxy the way .NET does
([`HttpClient.DefaultProxy`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.defaultproxy)). The
`<system.net><defaultProxy>` element of an `app.config` is a .NET Framework setting, and `vela` ignores it.

- `HTTPS_PROXY` (or `ALL_PROXY`) names the proxy. `NO_PROXY` lists the hosts to reach directly, comma-separated,
  with a leading dot for subdomains (`.contoso.com`).
- When none of them is set, .NET uses the system proxy settings on Windows and macOS, and connects directly on
  Linux.

```bash
export HTTPS_PROXY=http://proxy.contoso.com:8080      # PowerShell: $env:HTTPS_PROXY = "http://proxy.contoso.com:8080"
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

The Dataverse connection and the sign-in requests go through the proxy. With `--interactive`, the sign-in page opens
in your browser, which uses its own proxy settings. An unreachable proxy fails the command with exit code `6`, like
any connection failure. A proxy URL can carry credentials (`http://user:password@proxy:8080`), but the password then
sits in the environment of every process started from that shell.

`dotnet tool install` and `vela update` download the tool through NuGet, which has its own
[proxy settings](https://learn.microsoft.com/nuget/reference/nuget-config-file#config-section). The ConsoleApp that
`vela init --samples` generates is .NET 10 too and follows the same rules as `vela`. Plugin code runs in Dataverse,
where your local proxy settings play no part.

## Print a solution's publisher prefix (`vela solution show`)

`vela solution show <uniquename>` connects with the usual options and prints the customization prefix of the
solution's publisher. Use it to align a scaffold or a Custom API with the solution before the first `apply`:

```bash
vela solution show ZavaSales -i            # prints: zava
vela solution show ZavaSales -i --json     # {"solution":"ZavaSales","publisherPrefix":"zava"}
vela init --name ZavaSales --prefix Zava --publisher-prefix "$(vela solution show ZavaSales -i)" --solution ZavaSales
vela add customapi ApproveOrder --prefix "$(vela solution show ZavaSales -i)"
```

It is read-only, and `show` is its only subcommand.

## Reading execution logs (`vela logs`)

`vela logs` (alias `vela trace`) reads the execution diagnostics of a Dataverse environment, so you or a coding agent
can see why a plugin failed without opening the Power Platform UI. It is read-only and reads one of two sources:

- **Plug-in Trace Log** (default): what plugins write with `ctx.Trace(...)`, plus the line Vela writes when a
  handler starts. The log has rows only when plugin trace logging is turned on for the environment (see
  [Enable trace logging](https://learn.microsoft.com/power-apps/developer/data-platform/logging-tracing#enable-trace-logging)).
  When it finds nothing, `vela logs` says so and points to that setting.
- **System jobs** with `--jobs`: the `asyncoperation` rows, which record the error of a failed asynchronous step
  even when trace logging is off. Only failed jobs by default; `--all-statuses` shows every status.

```bash
# Recent plugin errors from the last hour (interactive login)
vela logs --errors --since 1h -i

# Why did my async step fail? Look at the system jobs.
vela logs --jobs --since 1d -i

# Machine-readable output for an agent
vela logs --since 2h --type AccountNamePlugin --json -i
```

| Option | Effect |
| --- | --- |
| `--top <n>` | Maximum number of rows, newest first (1 to 200, default 20). |
| `--since <when>` | A duration (`30m`, `2h`, `3d`) or a date. |
| `--type <name>` | Plugin type name (substring match). |
| `--message <name>` | SDK message. |
| `--entity <logical name>` | Table logical name. |
| `--correlation <guid>` | Correlation id. |
| `--errors` | Trace log only: rows with an exception. |
| `--jobs` / `--all-statuses` | Read system jobs instead of the trace log / include every status. |
| `--full` | Do not truncate the trace and exception text. |
| `--json` | Print `{ "mode", "count", "logs": [...] }`. The JSON always carries the full text. |
