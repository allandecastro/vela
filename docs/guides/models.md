# Early-bound generation (`vela models`)

Generate early-bound model classes over the **same Dataverse connection** as `plan`/`apply` (no separate
`pac auth`). Scope by **solution** (which `pac modelbuilder` can't) and/or an explicit table list:

```powershell
# from the solution's components, into <prefix>.Shared.Models.Generated (inferred from the project)
vela models --solution ZavaSales --url https://zava.crm.dynamics.com --interactive

# or an explicit list, with an explicit namespace/output
vela models --entities account,contact --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated --overwrite

# typed Request/Response message classes for Custom APIs
vela models --custom-apis zava_Recalculate --interactive

# security roles → a Roles class for the test project (real privileges + root role id)
vela models --roles "Salesperson,Sales Manager" --namespace Zava.Tests.Roles --out tests/Zava.Tests/Roles
vela models --solution ZavaSales --solution-roles --namespace Zava.Tests.Roles --out tests/Zava.Tests/Roles
```

Generated classes are `partial` (extend them in a sibling file), keep the primary id synced with `Entity.Id`,
carry XML doc (display name / description / constraints / lookup targets), DataAnnotations
(`[StringLength]`/`[Range]`, omit with `--no-annotations`) and two self-contained facet attributes that no
standard annotation can express - `[RequiredLevel(...)]` (the full four-value Dataverse required level, not a
lossy boolean) and `[LookupTargets(...)]` (a lookup / Customer / polymorphic column's target tables). The test
harness's `FakeEntityMetadata.From<T>()` recovers all of these by reflection, so a derived fake metadata carries
required level and lookup targets with no hand-written overrides. `vela models` turns choices into enums (local choices nested in
the table, global option sets as shared enums; multi-select choices become an `IList<Enum>`), type **File**
columns as `Guid?` (the `fileid`) and **Image** columns as `byte[]` (the thumbnail), and expose alternate keys as typed factories
(`Account.ByAccountNumberKey("A-001")` → a key-based `EntityReference`). `--custom-apis` instead generates typed
`OrganizationRequest`/`OrganizationResponse` message classes (call with `service.Execute(new zava_RecalculateRequest { … })`).
`--roles` (explicit names) / `--solution-roles` (every role in a solution) instead generate a `Roles` class **for the
test project** - each role's `Name`, root `Id` and a configurator carrying its real privileges - so tests drive the
[security model](security-model.md) with the actual role configuration.
Connection flags are the same as `plan`/`apply`
(`--connection-string`, or `--url` + `--tenant-id` + `--client-id` + `--client-secret`, or `--interactive`).

> Out of scope for now (use `pac modelbuilder` if you need them): relationship navigation properties and an
> `OrganizationServiceContext`/LINQ provider. See [`docs/LIMITATIONS.md`](../reference/limitations.md).
