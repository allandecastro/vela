<!-- What does this change, and why? Link the issue it closes, if any (e.g. "Closes #123"). -->

## Checklist

- [ ] `dotnet build Vela.sln -c Release` is warning-free and the tests pass (see [CONTRIBUTING.md](../CONTRIBUTING.md#build-and-test); on macOS/Linux, the `net462` projects run with Mono)
- [ ] Tests added or updated for the change; coverage and mutation gates still hold
- [ ] Generated step API regenerated if `MessageModel.cs` changed (`dotnet run --project tools/codegen/Vela.StepApiGen -- --check`)
- [ ] `CHANGELOG.md` entry under `## [Unreleased]`, in the existing heading of its kind and line (e.g. `### Fixed (CLI)`), for a user-visible change; none for CI, tests or internal refactors (a maintainer labels those `no-changelog`, see [CONTRIBUTING.md](../CONTRIBUTING.md#changelog))
- [ ] Docs updated for user-visible behaviour (`docfx docs/docfx.json --warningsAsErrors` builds)
- [ ] No secrets, credentials, connection strings, tenant/environment URLs or customer data in the code, tests or logs
