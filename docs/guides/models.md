# Early-bound generation (`vela models`)

Generate early-bound model classes over the **same Dataverse connection** as `plan`/`apply` (no separate
`pac auth`). Scope the **table list** by **solution** (which `pac modelbuilder` can't) and/or an explicit list.
`--solution` chooses *which tables* to generate (its Entity components), **not which columns**: each table is
generated with **all** its columns (full metadata, like `pac modelbuilder`). A column's solution membership is
orthogonal to wanting it in the model (standard columns are never in a solution; a column may be added only for
translation), so scoping columns to the solution would drop the ones you actually use.

```powershell
# from the solution's components, into <prefix>.Shared.Models.Generated (inferred from the project)
vela models --solution ZavaSales --url https://zava.crm.dynamics.com --interactive

# or an explicit list, with an explicit namespace/output
vela models --entities account,contact --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated --overwrite

# typed Request/Response message classes for Custom APIs
vela models --custom-apis zava_Recalculate --interactive

# security roles → a dependency-light Roles class in Shared.Models.Generated (real privileges + root role id)
vela models --roles "Salesperson,Sales Manager" --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated
vela models --solution ZavaSales --solution-roles --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated
```

Generated classes are `partial` (extend them in a sibling file), keep the primary id synced with `Entity.Id`,
carry XML doc (display name / description / constraints / lookup targets), DataAnnotations
(`[StringLength]`/`[Range]`, omit with `--no-annotations`) and two self-contained facet attributes that no
standard annotation can express - `[RequiredLevel(...)]` (the full four-value Dataverse required level, not a
lossy boolean) and `[LookupTargets(...)]` (a lookup / Customer / polymorphic column's target tables). The test
harness's `FakeEntityMetadata.From<T>()` recovers all of these by reflection, so a derived fake metadata carries
required level and lookup targets with no hand-written overrides. `vela models` turns choices into enums (local choices nested in
the table, global option sets as shared enums; multi-select choices become an `IList<Enum>`), types **File**
columns as `Guid?` (the `fileid`) and **Image** columns as `byte[]` (the thumbnail), and exposes alternate keys as typed factories
(`Account.ByAccountNumberKey("A-001")` returns a key-based `EntityReference`). `--custom-apis` instead generates typed
`OrganizationRequest`/`OrganizationResponse` message classes (call with `service.Execute(new zava_RecalculateRequest { … })`).
Connection flags are the same as `plan`/`apply`
(`--connection-string`, or `--url` + `--tenant-id` + `--client-id` + `--client-secret`, or `--interactive`).

## TypeScript form models (`--forms`)

> [!NOTE]
> Preview / work in progress. This is part of the new client-side initiative and may change.

`vela models --forms` generates **TypeScript `FormModel` files** for the scoped tables' published
**Main** and **Quick Create** forms, for the vela-client runtime to consume. It reads each form's FormXML and
joins it with the table metadata, so the emitted model carries only the fields, tabs, sections and subgrids
actually on that form, each typed by the same rules as the C# models (string/memo, the numeric family, boolean,
date, choice/multichoice, lookup with its targets, guid, file, image). The output is TypeScript, so `--lang` is
optional here and defaults to `ts` (`--lang ts` is still accepted).

```powershell
# every published Main / Quick Create form of the scoped tables, into ./src/generated
vela models --forms --entities account,contact --out src/generated --interactive

# machine-readable report of the generated files (for scripts and agents)
vela models --forms --solution ZavaSales --out src/generated --json
```

Each form becomes one `forms/<entity>/<form>.form.ts` const that `satisfies FormModel` (the folder is the table's
logical name; the file name drops the entity prefix), plus a shared `option-sets/` folder at the output root
(`option-sets/global.ts` for global choices, `option-sets/<entity>.ts` for a table's local choices) that the
choice fields reference. Generation is deterministic (fields follow the form's layout order,
option sets follow first-reference order) and all-or-nothing, and reuses the same scope (`--solution` /
`--entities`) and connection flags as the rest of `vela models`. Only published forms are read (no drafts), and
same-named forms of one table get disambiguated file names. The FormXML parser is deliberately tolerant (modern
forms carry attributes newer than the published `FormXml.xsd`), so real forms generate cleanly; strict XSD
validation is available as a separate best-effort aid, not a gate on generation.

## TypeScript table types (`--tables`)

> [!NOTE]
> Preview / work in progress. This is part of the new client-side initiative and may change.

`vela models --tables` generates **TypeScript record interfaces** for the scoped tables, so the
vela-client Web API query layer is typed without hand-writing `T` in `retrieveMultiple<T>('account').select(...)`.
It is the Web API twin of the C# early-bound models: one `tables/<logicalname>.ts` per table. As with `--forms`,
`--lang` is optional and defaults to `ts`.

```powershell
vela models --tables --entities account,contact --out src/generated --interactive
```

Each table becomes `export interface <Pascal> extends DataverseRecordMeta { ... }`, importing the same shared
`option-sets/` folder (`global.ts` + `<entity>.ts`) the `--forms` path emits (a table one level deep imports from
`../option-sets/...`). Columns map by type: string/memo to `string`, the whole numeric family
(integer, decimal, money, ...) to `number`, boolean to `boolean`, date/time to `string` (the Web API returns ISO
strings), choice to its enum and multi-select to an enum array, and file/image to `string`. Every non-key column
is optional and nullable (`name?: string | null`); the **primary key is required** (`accountid: string`). A
**lookup** is exposed as its GUID under `_<name>_value` (for example `_primarycontactid_value?: string | null`); a
**multi-target** lookup (Customer, Owner, or any lookup with more than one target) additionally carries a typed
logical-name annotation naming which table the row points at
(`'_<name>_value@Microsoft.Dynamics.CRM.lookuplogicalname'?: 'account' | 'contact'`), while a single-target lookup
omits it.

Each interface is paired with a same-named runtime descriptor (so the symbol merges type and value):

```ts
export const Account = defineTable<Account>({
  entity: 'account',
  primaryId: 'accountid',
  multiSelect: ['new_servicetypes'],
});
```

`defineTable` is imported from `vela-client/webapi`, and the `multiSelect` key is omitted when the table has no
multi-select columns. Generation is deterministic (columns in metadata order, primary key first) and reuses the
same scope and connection flags as the rest of `vela models`.

## Root barrel (`index.ts`)

Both TypeScript scopes also emit a root `<out>/index.ts` **barrel** that named-re-exports every generated symbol
(form-model consts, table interfaces + their `defineTable` consts, and option-set enums), so a consumer imports
from one place instead of many deep paths:

```ts
import { AccountMain, Contact, account_customertypecode } from './generated';
```

The re-exports are **named** (never `export *`), so esbuild still tree-shakes the unused ones; a name is exported
once (a table's shared type/value name, or any collision, never double-exports). The barrel aggregates what the
run produced under `--out`, and is emitted only when there is at least one symbol.

## Security roles (`--roles` / `--solution-roles`)

`--roles "Salesperson,Sales Manager"` (explicit names) or `--solution-roles` (every role in a `--solution`)
generates a `Roles` class into the **shared models project**, alongside the table models. Each role becomes
three members, keyed off its **root** role id (the stable id, identical across business units) and its real
privileges read from the environment:

```csharp
public static class Roles
{
    public const string SalespersonName = "Salesperson";
    public static readonly Guid SalespersonId = new Guid("1111...");   // the real root role id
    public static readonly (string Entity, string Level, string Privilege)[] Salesperson = new[] // its real privileges
    {
        ("account", "BusinessUnit", "Read"),
    };
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
> The generated `Roles` is dependency-light (BCL types only, no test dependency), so it lives with the shared
> runtime models. `Roles.XName` / `Roles.XId` are usable from plugin code at runtime, while the harness reads the
> grant data in tests via the `Role(name, id, grants)` overload. Its `Id` values are the real role ids, so a
> plugin that resolves a user's roles at runtime and the test that seeds them line up on the same GUIDs.

> Out of scope for now (use `pac modelbuilder` if you need them): relationship navigation properties and an
> `OrganizationServiceContext`/LINQ provider. See [Limitations reference](../reference/limitations.md).
