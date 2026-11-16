---
_description: "Connect the CLI: ambient Azure credential, client secret, connection string, interactive login, sovereign clouds"
---

# Authentication

`vela plan`, `apply`, `inspect`, [`models`](../guides/models.md), `logs` and `solution show` connect to a Dataverse
environment. They share the same connection options, so what this page shows for one command works for all of them.
For running these commands in a pipeline, see [Environments and CI](environments-secrets.md).

## Choose a method

| Method | Options | Use it for |
| --- | --- | --- |
| [Ambient Azure credential](#ambient-azure-credential) | `--url` alone | Local use after `az login`, and CI with a managed identity or workload identity. No secret to handle. |
| [Client id and secret](#client-id-and-client-secret) | `--url`, `--tenant-id`, `--client-id`, and a secret | Unattended runs with an app registration. |
| [Connection string](#connection-string) | `--connection-string`, or `DATAVERSE_CONNECTION_STRING` | You already have a Dataverse connection string. |
| [Interactive browser login](#interactive-browser-login) | `--interactive` (`-i`) | Local use. Signs in through the browser and caches the token. |

Vela picks the method in this order:

1. A connection string (`--connection-string` or `DATAVERSE_CONNECTION_STRING`), when one is set.
2. `--interactive`.
3. Client id and secret, when any of `--tenant-id`, `--client-id` or a client secret is given (all are then required).
4. The ambient Azure credential, when only `--url` is given.

With none of these, the command exits `2` and lists the options. Every method sends a credential to the Dataverse
URL, so the URL (including the one in a connection string) must be an absolute `https` URL; anything else exits `2`.

## Ambient Azure credential

When you pass only `--url`, with no connection string, no client-secret option, no `--interactive`, and neither
`DATAVERSE_CONNECTION_STRING` nor `DATAVERSE_CLIENT_SECRET` set, Vela gets a Dataverse token from
[`DefaultAzureCredential`](https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential). No browser
and no extra option:

```bash
az login   # once, if you are not signed in yet
vela plan  --packages-dir ./artifacts --url https://zava.crm.dynamics.com --solution ZavaSales
vela apply --packages-dir ./artifacts --url https://zava.crm.dynamics.com --solution ZavaSales --auto-approve
```

`DefaultAzureCredential` tries a chain of sources, including environment variables, workload identity, managed
identity and an `az login` session, so the same command works on a developer machine and in a pipeline. The
Microsoft page linked above lists the chain. When no source yields a token, the command exits `6`.

## Client id and client secret

For unattended runs with an app registration, pass the environment URL, the tenant, the application (client) id and a
client secret:

```bash
export DATAVERSE_CLIENT_SECRET=...   # from your secret store
vela plan --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id>

# or pipe the secret in
get-secret | vela plan --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> --client-secret-stdin
```

Supply the secret in one of three ways:

| Method | How | Notes |
| --- | --- | --- |
| Environment variable | `DATAVERSE_CLIENT_SECRET` | Recommended. Nothing on the command line. |
| Standard input | `--client-secret-stdin` | Recommended. Reads one line from piped stdin. On an interactive terminal it refuses rather than echo a typed secret. |
| Option | `--client-secret <secret>` | Works but warns: the secret is visible in shell history and process listings. |

When more than one is given, `--client-secret` wins over `--client-secret-stdin`, which wins over
`DATAVERSE_CLIENT_SECRET`. No value may contain `;`, which would corrupt the connection string Vela builds from them.

## Connection string

`--connection-string "<...>"` (or the `DATAVERSE_CONNECTION_STRING` environment variable) passes a full Dataverse
connection string. Vela checks that its URL is `https` and hands it unchanged to the Dataverse `ServiceClient`, so it
supports the authentication types that `ServiceClient` connection strings support; see
[Use connection strings](https://learn.microsoft.com/power-apps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect).

A connection string usually embeds a secret, so prefer the environment variable to the option. Vela redacts
`ClientSecret` and `Password` values from the errors it prints.

## Interactive browser login

For local work, add `--interactive` (`-i`) to any connected command:

```bash
vela plan --packages-dir ./artifacts --solution ZavaSales --interactive
```

- Without `--url`, Vela asks for the environment URL and offers the last one you used as the default.
- The first run opens the browser to sign in. The token is then cached, so later runs sign in silently.
- The cache is the file `msal.cache` in the Vela config folder (`~/.vela`, or `$VELA_CONFIG_DIR`): encrypted with
  DPAPI on Windows, and readable only by your user on macOS and Linux.
- `--fresh-login` ignores the cached account and opens the browser again.
- [`vela logout`](../guides/cli-overview.md#commands) removes the cached token and the remembered URL.

The browser prompts appear only on a real terminal, so a scripted run never waits on them.

### Sovereign clouds and custom app registrations

By default the browser login uses the public client id that Microsoft provides for Dataverse development samples and
the `https://login.microsoftonline.com/common` authority. Override them for a sovereign cloud or your own app
registration:

| Option | Purpose |
| --- | --- |
| `--auth-client-id <appId>` | The public client id of the browser login. |
| `--authority <url>` | The authority (sign-in endpoint) to sign in against. |

## Connection timeout

Every connected command uses a Dataverse request timeout of 10 minutes by default. On a very large environment, or on
the first run after a deploy, a single request can take longer. Raise the timeout with `--timeout <minutes>` (1 to
60):

```bash
vela apply --packages-dir ./artifacts --solution ZavaSales \
  --url https://zava.crm.dynamics.com --auto-approve --timeout 20
```

## Next steps

- [Environments and CI](environments-secrets.md): secrets, strict mode and output in pipelines.
- [Plan and apply](../guides/plan-apply.md): deploy your packages.
