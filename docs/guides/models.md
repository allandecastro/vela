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
Connection flags are the same as `plan`/`apply`
(`--connection-string`, or `--url` + `--tenant-id` + `--client-id` + `--client-secret`, or `--interactive`).

## Security roles (`--roles` / `--solution-roles`)

`--roles "Salesperson,Sales Manager"` (explicit names) or `--solution-roles` (every role in a `--solution`)
generates a `Roles` class **for the test project**. Each role becomes three members, keyed off its **root**
role id (the stable id, identical across business units) and its real privileges read from the environment:

```csharp
public static class Roles
{
    public const string SalespersonName = "Salesperson";
    public static readonly Guid SalespersonId = new Guid("1111...");   // the real root role id
    public static void Salesperson(RoleBuilder r) =>                   // its real privileges
        r.Grant("account", AccessLevel.BusinessUnit, Privilege.Read);
}
```

Drive the [security model](security-model.md) with it - pass all three so the harness seeds the **real** role
id into `systemuserroles`/`teamroles`, exactly what plugin logic reads at runtime:

```csharp
var svc = new InMemoryOrganizationService()
    .WithCurrentUser(alice)
    .WithSecurity(s => s
        .BusinessUnit(salesBu)
        .Role(Roles.SalespersonName, Roles.SalespersonId, Roles.Salesperson)
        .User(alice, salesBu, Roles.SalespersonName));
```

> [!NOTE]
> The generated `Roles` references `Vela.Testing.Security` (for `RoleBuilder`), so it lives in the **test**
> project, not the shared runtime models. Its `Id` values are the real role ids, so a plugin that resolves a
> user's roles at runtime and the test that seeds them line up on the same GUIDs.

> Out of scope for now (use `pac modelbuilder` if you need them): relationship navigation properties and an
> `OrganizationServiceContext`/LINQ provider. See [Limitations reference](../reference/limitations.md).

> Out of scope for now (use `pac modelbuilder` if you need them): relationship navigation properties and an
> `OrganizationServiceContext`/LINQ provider. See [Limitations reference](../reference/limitations.md).
