# Sign the plugin package (`vela sign`)

A plugin that uses a managed identity must ship in a signed plugin package (`.nupkg`); see
[Set up managed identity for Power Platform](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity)
for the platform requirements. `vela sign` signs a package by calling `dotnet nuget sign`, so it runs wherever the
.NET SDK runs: Windows, Linux and macOS.

This page covers the command, the build integration that `vela init --managed-identity` generates, and how signed
packages work with `vela apply`. To link the package to a managed identity at deployment, see
[Managed identity](managed-identity.md).

## Sign a package

```bash
# Certificate in the CurrentUser\My store, by SHA-256 fingerprint
vela sign --package artifacts/zava_Zava.Integrations.1.0.0.nupkg --fingerprint <sha256-fingerprint>

# PFX file, password from the Windows Credential Manager (Windows only)
vela sign --package artifacts/zava_Zava.Integrations.1.0.0.nupkg --pfx cert.pfx --pfx-credential <credential-name>

# Base64 PFX and password from environment variables (CI)
vela sign --package artifacts/zava_Zava.Integrations.1.0.0.nupkg --pfx-base64-env PFX_B64 --pfx-password-env PFX_PW
```

Give exactly one certificate source:

| Source | Option | Notes |
| --- | --- | --- |
| Certificate store | `--fingerprint <hex>` | The certificate's SHA-256 fingerprint. Recommended: no secret on disk or on the command line. |
| PFX file | `--pfx <file>` | Needs a password (below). |
| Base64 PFX | `--pfx-base64-env <VAR>` | The variable holds the PFX as base64, typically a CI secret. Needs a password (below). |

A PFX needs a password from one of:

| Option | Notes |
| --- | --- |
| `--pfx-password-env <VAR>` | The variable holds the password. |
| `--pfx-credential <name>` | A generic credential in the Windows Credential Manager. Windows only. |

Other options:

- `--timestamp <url>`: the RFC 3161 timestamp server. Default `http://timestamp.digicert.com`.
- `--json`: print `{ "package", "signed", "method", "timestampUrl" }` on success.

A package that is already signed is signed again (the signature is overwritten), so a rebuilt package can be re-signed.

### Keep secrets off the command line

Pass secrets by environment variable name. `--pfx-base64-env` and `--pfx-password-env` read the value from the named
variable, so it never appears on a command line, where other users of a shared host can read it (`ps`, `/proc`). The
literal `--pfx-base64 <content>` and `--pfx-password <password>` still work but print a warning.

`dotnet nuget sign` accepts a PFX password only as a command-line argument, so `vela sign` never passes it one.
Instead, `vela sign` opens the PFX itself, writes a passwordless copy to an owner-only temporary file, hands that file
to `dotnet nuget sign`, and deletes it when signing ends. A base64 PFX takes the same path.

## Sign during the build

`vela init --managed-identity` adds the Integrations project (with or without `--samples`) and a
`SignPluginPackage` MSBuild target to it. The target runs after `Pack` and:

- does nothing until you configure a certificate source;
- is skipped in CI (when `GITHUB_ACTIONS` or `TF_BUILD` is set), so the pipeline owns its signing step and the package
  is not signed twice.

Configure it with MSBuild properties:

```bash
dotnet build src/Zava.Integrations -c Release -p:PluginSigningFingerprint=<sha256>
dotnet build src/Zava.Integrations -c Release -p:PluginSigningCertificate=cert.pfx -p:PluginSigningCredential=<credential-name>
```

A `PluginSigningPassword` property reaches `vela sign` through an environment variable, not its command line. The
README of the generated solution lists every property.

`vela init --managed-identity` needs a valid publisher prefix, from `--publisher-prefix` or derived from `--prefix`,
because the signed package must already carry it (see below).

## Signed packages and `vela apply`

Dataverse imports a plugin package only when its name carries the solution publisher prefix. When the name lacks it,
`vela apply` adds the prefix by rewriting the package id inside the `.nupkg`. That rewrite changes the archive, which
invalidates any signature on it.

So `apply` checks for a signature first. When a signed package would need the rename, `apply` stops before uploading,
with a message that names the expected package id. To deploy a signed package, build and sign it with the prefixed
name already in place (package id `zava_Zava.Integrations` for prefix `zava`). `apply` then needs no rename and
uploads the signed bytes unchanged.

See [Plan and apply](plan-apply.md#choose-the-solution) for how `apply` names packages.
