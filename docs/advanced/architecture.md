# Architecture

This page describes how the Vela framework repository is laid out and the rules that
keep its concerns separated. It is aimed at people building or contributing to the
framework itself; if you just want to *use* Vela, start with the
[CLI overview](../guides/cli-overview.md).

## Solution layout

```text
Vela.sln
src/
  Vela.Plugins/   # Framework runtime, contracts, primitives, and shared definitions
  Vela.Cli/       # CLI (command `vela`) for manifests and Dataverse sync
  Vela.Testing/   # Test harness package
tests/
  Vela.Tests/     # Unit tests
.github/workflows/ci.yml
```

## Architecture rules

Keep these rules in mind when adding new code:

1. **Core** (`Vela.Plugins`) contains framework-level runtime code, contracts, primitives, and generic immutable definitions.
2. **Tools** (`Vela.Cli`) contains the shared CLI used locally and in CI.
3. Generated early-bound model code belongs to the **consumer/plugin repo**, not this framework repo.
4. Use the framework APIs to build plugins and metadata generators; do not keep sample app code in the release path.
5. Keep registration metadata and runtime execution concerns separate.
