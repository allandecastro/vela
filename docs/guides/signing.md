# Sign the plugin package (`vela sign`)

When a plugin uses a **managed identity**, Dataverse requires the plugin **package** (`.nupkg`) to carry a
trusted signature. `vela sign` applies one by calling `dotnet nuget sign` (so it runs anywhere the .NET SDK
does - Windows, Linux, macOS):

```powershell
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --fingerprint <sha256-fingerprint>
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --pfx cert.pfx --pfx-credential <cred>
vela sign --package artifacts/Zava.Integrations.1.0.0.nupkg --pfx-base64-env PFX_B64 --pfx-password-env PFX_PW
```

Certificate sources (exactly one): `--fingerprint` (cert in `CurrentUser\My` - recommended, no secret on
disk), `--pfx <file>` with a password or `--pfx-credential` (a Windows Credential Manager entry), or the base64
`.pfx` content (a CI secret, decoded to an owner-only temp file and deleted after). Optional `--timestamp`
(default `http://timestamp.digicert.com`).

Pass secrets by **environment-variable name**: `--pfx-base64-env <VAR>` and `--pfx-password-env <VAR>` read the
value from that variable, so it never appears on a command line, where other users of a shared host can read it
(`ps`, `/proc`). The literal `--pfx-base64 <content>` / `--pfx-password <pw>` still work but warn. `dotnet nuget
sign` only accepts a PFX password as a command-line argument, so `vela sign` never gives it one: it opens the PFX
itself and passes the tool a passwordless copy in an owner-only temporary file, deleted once signing ends.

`vela init --samples --managed-identity` wires this into the build of the **Integrations** project: a `SignPluginPackage`
MSBuild target runs after `Pack`, **inert until a certificate source is configured** and **skipped inside CI**
(`GITHUB_ACTIONS` / `TF_BUILD`) so a pipeline owns its own sign step - no double-signing. Configure it with
MSBuild properties, e.g. `dotnet build src/Zava.Integrations -c Release
-p:PluginSigningFingerprint=<sha256>` (or `-p:PluginSigningCertificate=path.pfx -p:PluginSigningCredential=<cred>`).
A `PluginSigningPassword` reaches `vela sign` through an environment variable, not its command line. The
generated solution's README documents the full property set.

## Signed packages and `apply --solution`

`apply --solution <name>` adds the solution's publisher prefix to the plugin package name, which it does by
rewriting the `.nupkg`. That rewrite changes the archive, so it would invalidate a signature already applied by
`vela sign` (Dataverse rejects the import with `0x80040265`, "the package is signed but its signature does not
cover the package content"). `apply` detects a signed package up front and fails with a clear message rather
than uploading one Dataverse will reject. To deploy a signed package into a solution, build and sign it with the
prefixed name already in place (so no rename is needed), or apply it without `--solution`. See
[plan → apply](plan-apply.md).
