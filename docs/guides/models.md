# Early-bound generation (`vela models`)

Generate early-bound model classes over the **same Dataverse connection** as `plan`/`apply` (no separate
`pac auth`). Scope the **table list** by **solution** (which `pac modelbuilder` can't) and/or an explicit list.
`--solution` chooses *which tables* to generate (its Entity components), **not which columns**: each table is
generated with **all** its columns (full metadata, like `pac modelbuilder`). A column's solution membership is
orthogonal to wanting it in the model (standard columns are never in a solution; a column may be added only for
translation), so scoping columns to the solution would drop the ones you actually use.

```powershell
# from the solution's components, into <prefix>.Shared.Models.Generated (namespace inferred from the project,
# or from the solution's .sln when run at the solution root; override with --namespace)
vela models --solution ZavaSales --url https://zava.crm.dynamics.com --interactive

# or an explicit list, with an explicit namespace/output
vela models --entities account,contact --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated --overwrite

# typed Request/Response message classes for Custom APIs (into Messages/)
vela models --custom-apis zava_Recalculate --interactive

# ...or every Custom API in the environment, with no value
vela models --custom-apis --interactive

# ...every Custom API except the ones you authored in code (pass your built package)
vela models --custom-apis --package bin/Release/Zava.Plugins.nupkg --interactive

# typed Request/Response classes for specific SDK messages (into Messages/)
vela models --messages AddToQueue,RouteTo --interactive

# ...or every consumable message in the environment (filtered), with no value
vela models --messages --interactive

# security roles → a dependency-light Roles class in Shared.Models.Generated (real privileges + root role id)
vela models --roles "Salesperson,Sales Manager" --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated
vela models --solution ZavaSales --solution-roles --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated
```

## What the generated table classes contain

`--solution` / `--entities` generate `partial` entity classes (extend them in a sibling file) with:

- **Primary id kept in sync** with `Entity.Id`. The `Id` override carries `[AttributeLogicalName]` naming the primary-id column (e.g. `"accountid"`), so
  `a => a.Id` selects the primary key column in `FilterOn`, images and typed queries.
- **XML doc** on each property (display name, description, constraints, lookup targets).
- **DataAnnotations** (`[StringLength]` / `[Range]`); omit with `--no-annotations`.
- **Two facet attributes** no standard annotation can express: `[RequiredLevel(...)]` (the full four-value
  Dataverse required level, not a lossy boolean) and `[LookupTargets(...)]` (a lookup / Customer / polymorphic
  column's target tables).
- **Choices as enums** (local choices nested in the table, global option sets as shared enums; multi-select
  choices become an `IList<Enum>`).
- **File** columns typed as `Guid?` (the `fileid`) and **Image** columns as `byte[]` (the thumbnail).
- **Alternate keys as typed factories**: `Account.ByAccountNumberKey("A-001")` returns a key-based
  `EntityReference`. Each key column is a **typed parameter** (a composite key takes one per column, in order), so
  wrong types or arity fail at compile time. Use the reference to set a lookup
  (`contact.ParentCustomerId = Account.ByAccountNumberKey("A-001")`), to read the record with
  `ctx.Service.RetrieveByKey<Account>(Account.ByAccountNumberKey("A-001"), a => a.Name)`, or to create-or-update
  by business key with `ctx.Service.UpsertByKey(account, Account.ByAccountNumberKey("A-001"))` (returns whether
  a record was created). The factory is a method (not a property), so it can never be used as a column in
  `FilterOn`/`Select`.
- **Typed relationship navigation properties** (see below).

The test harness's `FakeEntityMetadata.From<T>()` recovers all of this by reflection, so a derived fake metadata
carries required level and lookup targets with no hand-written overrides.

## Relationship navigation properties

When you generate a table together with its related tables, each class also gets typed relationship navigation
properties, annotated with `[RelationshipSchemaName("...")]`:

- **N:1** (a lookup on this table) becomes a single typed parent, named after the lookup without its `Id` suffix,
  for example `PrimaryContactId` (the foreign key) gains a `PrimaryContact` of type `Contact`. Only **single-target**
  lookups get a nav; a Customer / Owner / polymorphic lookup keeps just its `EntityReference` (there is no single
  target type to expose).
- **1:N** (children of this table) becomes a typed collection, for example `account.Contacts` of type
  `IList<Contact>`.
- **N:N** (many-to-many) becomes a typed collection of the other table, marked `ManyToMany` (its members are
  associated, not created). Self-referential N:N is not generated (the two directions would need distinct names).

A relationship whose target table is not part of the same generation is skipped (its class would not exist).

### Intersect (N:N join) tables (`--intersect`)

An N:N relationship is backed by a hidden **intersect table** (the join table, e.g. `listmember`). To query that
join directly with a typed query, you need its model, but its logical name is rarely known. Add `--intersect` and
`vela models` discovers the intersect tables of the scoped tables from their N:N relationship metadata and generates
them as entity classes too:

```bash
vela models --solution ZavaSales --intersect --namespace Zava.Shared.Models.Generated
```

It is opt-in and **one level** - the intersect tables of the scoped tables only, not transitively - and de-duplicates
and reports how many it added. It needs live metadata, so it cannot be combined with `--from-solution`. It does not
pull in the N:N *other-end* tables; add those to `--entities` if you also want their nav properties. System tables
(for example `activityparty`, `systemuser`) are already generatable by naming them in `--entities`.

A navigation property is **not a column**: it cannot be used in `FilterOn` / `Select` / `Where` / `OrderBy` /
`WithPreImage` (select the lookup foreign-key column instead, e.g. `a.PrimaryContactId`). The analyzer
[VELA010](../reference/analyzers.md#vela010) flags this at build time, and the runtime resolvers reject it.

**Deep insert.** Populate the 1:N collections and create the whole graph in one transaction with
`CreateWithRelated` (from `Vela.Context`), which projects the collections into `Entity.RelatedEntities` for you:

```csharp
using Vela.Context;

var account = new Account { Name = "Contoso" };
account.Contacts.Add(new Contact { FirstName = "Ada", LastName = "Lovelace" });
account.Contacts.Add(new Contact { FirstName = "Grace", LastName = "Hopper" });

Guid accountId = ctx.Service.CreateWithRelated(account);   // account + two contacts, one transaction
```

**Relationship-aware read (`Include`).** Retrieve a record together with related records in one call and have the
navigation properties hydrated, with `RetrieveWithRelated` (from `Vela.Context`). The target can be an id or an
alternate-key reference:

```csharp
using Vela.Context;

SalesOrder order = ctx.Service.RetrieveWithRelated<SalesOrder>(
    new EntityReference(SalesOrder.EntityLogicalNameValue, orderId),
    q => q.Include(o => o.Parent_Account)     // N:1 parent, typed
          .Include(o => o.Order_Details),     // 1:N children, typed
    o => o.Name);                             // root columns (optional; all columns when omitted)

string customer = order.Parent_Account?.Name;
decimal total = order.Order_Details.Sum(l => l.LineTotal ?? 0);
```

This is a **single-record** read: Dataverse nests related records on `Retrieve` (not `RetrieveMultiple`), **one
level deep**, and each related query is subject to the **5000-row page cap**, so use it for bounded relationships.
For a set of records, or deeper graphs, run a second query on the children and stitch by the parent lookup.
`Include` also works for **N:N** relationships (it reads the associated records).

**Associate (N:N).** An N:N link connects records that already exist, so you associate rather than create: populate
the N:N collection with existing records (each with its id) and call `ctx.Service.AssociateRelated(account)`,
which associates every populated N:N collection. (`CreateWithRelated` deliberately ignores N:N collections - it only
deep-inserts 1:N children.)

## Verify drift against the live schema (`--verify`)

The generated model is compile-time-safe against the *model*, not the org. If a column is deleted or renamed in
Dataverse but the model is not regenerated, the code still compiles and a typed read (including a typed image
projection) returns a silent `null` at runtime. `--verify` closes that gap:

```bash
# Compare the entity model already on disk against the live schema. Writes nothing.
vela models --verify --entities account,contact --url https://org.crm.dynamics.com
```

It regenerates the scoped entity models from live metadata in memory and compares them, column by column, against
the files already on disk (found via the same `--out` / namespace resolution used to generate them). For each
table it reports:

- **removed in Dataverse** - a column the local model still has but the live schema no longer does (the runtime
  `null` blind spot);
- **type changed** - a column whose generated member type differs (for example `int?` to `decimal?`);
- **added in Dataverse** - a column present live but missing from the local model (regenerate to include it).

`--verify` writes nothing and exits `5` when any table is out of sync (and `0` when the model matches), so it
drops straight into CI to fail a build on a stale model. Add `--json` for a machine-readable report. It checks
the C# early-bound entity model only; `--custom-apis` / `--messages` / `--roles` scopes are ignored.

## Offline generation from an exported solution (`--from-solution`)

Generate the early-bound classes and option-set enums with no Dataverse connection, from the `customizations.xml`
inside an exported (unmanaged) solution zip. Useful for migrations/onboarding where a connection is not yet
available, or as a fallback when the org is unreachable.

```bash
# Export the solution once (any tool), then generate offline.
vela models --from-solution ./MySolution.zip --namespace Acme.Shared.Models --out src/Shared/Models/Generated

# Narrow to specific tables from the zip.
vela models --from-solution ./MySolution.zip --entities adc_widget,adc_part --namespace Acme.Shared.Models
```

The offline path produces the same C# entity classes and enums as the online path (it feeds the same generator).
It scopes to the custom tables the solution carries; system tables are exported as shells with no columns and are
skipped. Because an exported solution does not carry everything live metadata does, some cases degrade gracefully
rather than fail:

- a column of a type the generator does not map is skipped;
- a lookup keeps no typed `[LookupTargets]` (the export stores numeric type codes, not logical names) - it stays a
  plain `EntityReference`;
- a global option set not present in the file falls back to a raw `OptionSetValue`;
- relationship navigation properties are not emitted in this first version.

Offline output is C# only (`--forms` / `--tables` and `--verify` need live metadata). When the org is reachable,
prefer the connected path - it carries the full metadata (typed lookup targets, relationships).

## Custom API message classes (`--custom-apis`)

`--custom-apis` generates typed `OrganizationRequest`/`OrganizationResponse` classes into a `Messages/` subfolder:

```csharp
var response = service.Execute(new zava_RecalculateRequest { AccountId = id })
    .ToResponse<zava_RecalculateResponse>();
```

Each also gets a **registrability-gated `On<Message>()` step extension**: bound to the API's entity when it has
one (compile-time entity applicability), async-only when the API's `allowedcustomprocessingsteptype` is AsyncOnly
(only `.Async()` compiles), and omitted for a None/private API. A required request parameter (`isoptional = false`)
carries a `[Required]` attribute so rebuilding the manifest preserves its optionality, and a parameter with a table
(`logicalentityname`) carries `[LogicalEntityName("...")]` so the table is preserved too; response properties are
always present. See [Write a Custom API](custom-apis.md).

**Scoping.** With **no value**, `--custom-apis` generates *every* Custom API in the environment (the platform's own
included, often hundreds of files) and prints a warning. Narrow it with a targeted list (`--custom-apis a,b,c`,
never filtered) or exclude the ones you authored in code by passing your built package (`--package <file.nupkg>` /
`-p`, repeatable; `--packages-dir <folder>`; `--package-assembly <name>` to disambiguate): their unique names are
scanned from the package (the same offline scan `plan`/`apply` use) and removed, with a note of how many were
excluded.

## SDK message classes (`--messages`)

`--messages a,b,c` generates typed `Request`/`Response` classes for any SDK message (`sdkmessage`), into
`Messages/`. With **no value** it generates every *registerable* message (Microsoft's "available events":
non-private messages with a visible, custom-step-allowed `sdkmessagefilter`), skipping messages backed by a
Custom API and messages that already ship an SDK request class - the core `Microsoft.Xrm.Sdk.Messages.*` types
and the Sales/business `Microsoft.Crm.Sdk.Messages.*` types (matched against a curated, non-exhaustive supplement;
an unlisted one just makes a harmless duplicate).

A class is generated per **request**, named after the request: that is the name Dataverse executes, which is not
always the `sdkmessage` name. `--messages Send` generates `SendEmailRequest`, `SendFaxRequest` and
`SendTemplateRequest` (each bound to its own table), and `--messages Win` generates `WinOpportunityRequest` and
`WinQuoteRequest`; a request sent as `"Send"` fails with "Request not supported". You can also pass the request
name (`--messages WinQuote`) to generate that request only.

Alongside each `XxxRequest`/`XxxResponse`, `--messages` emits an **entity-constrained `On<Message>()` step
extension** per applicable entity (from the message's `sdkmessagefilter` `primaryobjecttypecode`), giving
**compile-time entity applicability**: a generated `OnMyMessage()` only compiles inside the `EntityPluginBase<T>`
for the table its `sdkmessagefilter` targets, exactly like the curated `On<Message>()` methods (below). A global
message (`primaryobjecttypecode` `none`) or one applying to more than eight entities emits a single generic
`On<Message><TEntity>()` instead. The requests also pair with the permissive `OnMessage(...).Handle<TRequest>()`
and with `On<TRequest>()` directly.

### Using a standard message (`QualifyLead`)

Many standard Sales/business messages are **curated by Vela out of the box**, so their typed step method and
context already exist with **no `vela models` run needed**. `QualifyLead` is one: register a step on it directly,
and the handler receives the typed `QualifyLeadRequest`/`QualifyLeadResponse`.

```csharp
public sealed partial class RouteQualifiedLead(string unsecure, string secure) : EntityPluginBase<Lead>(unsecure, secure)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnQualifyLead().PostOperation().Sync().Handle(OnQualified);

    private void OnQualified(IQualifyLeadPostContext<Lead> ctx)
    {
        QualifyLeadRequest request = ctx.Request;                       // strongly typed
        QualifyLeadResponse response = ctx.Response;                    // the created records
        foreach (EntityReference created in response.CreatedEntities)   // account / contact / opportunity
        {
            // e.g. stamp or route each record the qualification created
        }
    }
}
```

You can also call the message from your own code with the SDK request:

```csharp
var response = (QualifyLeadResponse)service.Execute(new QualifyLeadRequest
{
    LeadId = new EntityReference(Lead.EntityLogicalNameValue, leadId),
    CreateAccount = true,
    Status = new OptionSetValue(3), // Qualified
});
```

Use `--messages` only for a message Vela does **not** curate: it generates the same typed `Request`/`Response`
and the entity-constrained `On<Message>()` extension for it. (The full curated list is in
[Write a plugin](plugins.md).)

## Connection flags

Same as `plan`/`apply`: `--connection-string`, or `--url` + `--tenant-id` + `--client-id` + `--client-secret`, or
`--interactive`. Early-bound entity types are assumed to be generated into the same namespace
(`vela models --tables`).

## TypeScript form models (`--forms`)

> [!NOTE]
> Preview / work in progress. This is part of the new client-side initiative and may change.

`vela models --forms` generates **TypeScript `FormModel` files** for the scoped tables' published
**Main** and **Quick Create** forms, for the vela-client runtime to consume. It reads each form's FormXML and
joins it with the table metadata, so the emitted model carries only the fields, tabs, sections and subgrids
actually on that form, each typed by the same rules as the C# models (string/memo, the numeric family, boolean,
date, choice/multichoice, lookup with its targets, guid, file, image).

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
It is the Web API twin of the C# early-bound models: one `tables/<logicalname>.ts` per table.

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

**Activity tables** (email, appointment, ...) expose their party lists (`to` / `from` / `cc`,
`requiredattendees`, ...) not as per-slot columns but as one `activityparty` collection distinguished by
`participationtypemask`. The record therefore carries a single `<entity>_activity_parties?: ActivityParty[] | null`
member (in place of the individual party-list attributes), and `defineTable` adds `activityParties:
'<entity>_activity_parties'`. On the consumer side you read them through `expand`, then filter by mask:

```ts
const { entities } = await createWebApi()
  .retrieveMultiple(Email)
  .select('subject')
  .expand<ActivityParty[]>('email_activity_parties', ['participationtypemask', '_partyid_value'])
  .execute();

const to = entities[0]?.email_activity_parties
  ?.filter((p) => p.participationtypemask === ParticipationType.ToRecipient);
```

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

`--forms` and `--tables` can be **combined in one run**, so form models, table types, the shared `option-sets/`
folder and a single complete barrel are emitted together (a `--forms`-only or `--tables`-only run only barrels its
own scope). Regenerating into a folder that already has generated files needs `--overwrite`:

```powershell
vela models --forms --tables --entities account,contact --out src/generated --overwrite
```

## Security roles (`--roles` / `--solution-roles`)

`--roles "Salesperson,Sales Manager"` (explicit names) or `--solution-roles` (every role in a `--solution`)
generates a `Roles` class into the **shared models project**, alongside the table models. Each role becomes
three members, keyed off its **root** role id (the stable id, identical across business units) and its real
privileges read from the environment. A `--roles` name that is not found in the environment is warned about and
skipped, and the roles that did resolve are still generated; the command only fails when none of the requested
roles exist.

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
