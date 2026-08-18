# The vela CLI

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command.

Install from your NuGet feed (once published):

```powershell
dotnet tool install --global Vela.Cli
```

Or install from a locally built package:

```powershell
dotnet pack .\src\Vela.Cli -c Release -o .\artifacts\cli
dotnet tool install --global --add-source .\artifacts\cli Vela.Cli
```

Then run it (update later with `dotnet tool update --global Vela.Cli`):

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
| `vela apply --execute` | Apply those operations (idempotent upsert/delete). See [CLI automation](plan-apply.md). |
| `vela sign` | [Sign a plugin package](signing.md) (required for managed-identity plugins). |

> [!NOTE]
> prefer the `DATAVERSE_CLIENT_SECRET` environment variable or `--client-secret-stdin`
> over `--client-secret`, which is exposed to shell history and process listings (the CLI warns when
> you use it). For sovereign clouds / custom app registrations under `--interactive`, override the
> MSAL login with `--auth-client-id` and `--authority`. Add `--verbose` for human-readable summaries
> and full exception stack traces on failure.
