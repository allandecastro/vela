# Early-bound models (`vela models`)

`vela models` generates typed C# classes from your Dataverse environment: early-bound table classes, typed
Custom API and SDK message classes, and a `Roles` class for security roles. It can also check committed models
against the environment (`--verify`), generate offline from an exported solution (`--from-solution`), and, in
preview, generate TypeScript models for form scripts.

It connects with the **same connection flags** as `plan` and `apply` (no separate `pac auth`). Run it after
[scaffolding a solution](scaffold.md); the plugin and test pages then use the generated classes.

## Quick reference

```powershell
# Tables from a solution's components, into <Prefix>.Shared.Models.Generated
vela models --solution ZavaSales --url https://zava.crm.dynamics.com --interactive

# An explicit table list, with an explicit namespace and output folder
vela models --entities account,contact --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated --overwrite

# Typed Request/Response classes for Custom APIs (one, or every one when the value is omitted)
vela models --custom-apis zava_Recalculate --interactive
vela models --custom-apis --package artifacts/zava_Zava.Plugins.1.0.0.nupkg --interactive

# Typed Request/Response classes for SDK messages, by request name
vela models --messages AddToQueue,RouteTo --interactive

# Security roles: a dependency-light Roles class
vela models --roles "Salesperson,Sales Manager" --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated
vela models --solution ZavaSales --solution-roles --namespace Zava.Shared.Models.Generated --out src/Zava.Shared/Models/Generated

# Check the committed models against an environment (writes nothing, exit 5 on drift)
vela models --verify --entities account,contact --url https://zava.crm.dynamics.com
```

Run `vela models --help` for the full option list.

## Scope and output

Pick at least one scope. Scopes combine in one run; with none, the command refuses to generate every table.

| Option | Generates |
| --- | --- |
| `--solution <uniquename>` | A class for each table in the solution's components. |
| `--entities <a,b,c>` | A class for each named table (logical names). System tables such as `systemuser` or `activityparty` work too. |
| `--intersect` | Also the N:N intersect tables of the scoped tables (see [Intersect tables](#intersect-nn-join-tables---intersect)). |
| `--custom-apis [a,b,c]` | [Custom API message classes](#custom-api-message-classes---custom-apis). |
| `--messages [a,b,c]` | [SDK message classes](#sdk-message-classes---messages). |
| `--roles <a,b,c>` / `--solution-roles` | A [`Roles` class](#security-roles---roles----solution-roles). `--solution-roles` takes every role in `--solution`. |

`--solution` chooses **which tables**, not which columns: each table is generated with **all** its columns, like
`pac modelbuilder`. Standard columns are never in a solution, and a column can be in a solution for unrelated
reasons, so scoping columns to the solution would drop columns you use.

**Namespace and folder.** Without `--namespace`, the namespace is `<Prefix>.Shared.Models.Generated`, where
`<Prefix>` comes from the project in the current folder (or from the `.sln` when you run at the solution root,
preferring the `.Shared` project); `--project <csproj>` names the project explicitly. Without `--out` or
`--namespace`, the files go to that project's `Models/Generated` folder; otherwise to `--out` (default: the current
folder).

**Other output options.**

- `--overwrite` replaces existing files. Without it, the run fails before writing anything if a file exists.
- `--no-annotations` omits the DataAnnotations attributes.
- `--json` prints a machine-readable report on stdout (see [Machine-readable output](../reference/machine-readable-output.md)).

**Connection.** The same flags as `plan` and `apply`: `--url` alone (your ambient Azure credential),
`--connection-string`, `--url` with `--tenant-id`, `--client-id` and the secret in `DATAVERSE_CLIENT_SECRET` (or
`--client-secret-stdin`), or `--interactive`. See [Authentication](../configuration/authentication.md).

## What the generated table classes contain

`--solution` and `--entities` generate one `partial` class per table (extend it in a sibling file) with:

- **The primary id kept in sync** with `Entity.Id`. The `Id` override carries `[AttributeLogicalName]` with the
  primary-id column (for example `"accountid"`), so `a => a.Id` selects the primary key in `FilterOn`, images and
  typed queries.
- **XML documentation** on each property: display name, description, constraints and lookup targets.
- **DataAnnotations** (`[StringLength]`, `[Range]`), unless you pass `--no-annotations`.
- **Two facet attributes** that no standard annotation expresses: `[RequiredLevel(...)]` (the four Dataverse
  required levels) and `[LookupTargets(...)]` (the target tables of a lookup, Customer or polymorphic column).
- **Choices as enums.** A local choice is nested in the table class, a global choice is a shared enum, and a
  multi-select choice is an `IList<TEnum>`.
- **File** columns as `Guid?` (the file id) and **Image** columns as `byte[]` (the thumbnail).
- **Alternate keys as typed factories.** `Account.ByAccountNumberKey("A-001")` returns a key-based
  `EntityReference`, with one typed parameter per key column, so a wrong type or arity fails at compile time. Use
  it to set a lookup (`contact.ParentCustomerId = Account.ByAccountNumberKey("A-001")`), to read
  (`ctx.Service.RetrieveByKey<Account>(Account.ByAccountNumberKey("A-001"), a => a.Name)`), or to create or update
  by business key (`ctx.Service.UpsertByKey(account, Account.ByAccountNumberKey("A-001"))`, which returns
  `(Created, Target)`). The factory is a method, so it can never be used as a column in `FilterOn` or `Select`.
- **Typed relationship navigation properties** (next section).

In tests, `FakeEntityMetadata.From<T>()` reads these attributes by reflection, so fake metadata carries the
required levels and lookup targets with no hand-written overrides.

## Relationship navigation properties

When you generate a table together with its related tables, its class also gets typed navigation properties,
each marked `[RelationshipSchemaName("...")]`:

- **N:1** (a lookup on this table) becomes a typed parent named after the lookup without its `Id` suffix:
  `PrimaryContactId` gains `PrimaryContact` of type `Contact`. Only **single-target** lookups get one; a Customer,
  Owner or polymorphic lookup keeps only its `EntityReference`.
- **1:N** (children of this table) becomes a typed collection, for example `account.Contacts` of type
  `IList<Contact>`. When several relationships point at the same child table, the later ones are named after
  the relationship schema name.
- **N:N** becomes a typed collection of the other table, marked `ManyToMany = true` (its members are associated,
  not created). A self-referential N:N is not generated.

A relationship whose target table is not in the same run is skipped, because its class would not exist.

A navigation property is **not a column**. It cannot be used in `FilterOn`, `Select`, `Where`, `OrderBy` or
`WithPreImage`; select the lookup column instead (`a.PrimaryContactId`). The analyzer
[VELA010](../reference/analyzers.md#vela010) flags this at build time, and the runtime resolvers reject it.

### Create a record with its children (`CreateWithRelated`)

Populate the 1:N collections and create the whole graph in one transaction with `CreateWithRelated` (in
`Vela.Context`), which projects the collections into `Entity.RelatedEntities`:

```csharp
using Vela.Context;

var account = new Account { Name = "Contoso" };
account.Contacts.Add(new Contact { FirstName = "Ada", LastName = "Lovelace" });
account.Contacts.Add(new Contact { FirstName = "Grace", LastName = "Hopper" });

Guid accountId = ctx.Service.CreateWithRelated(account);   // account + two contacts, one transaction
```

`CreateWithRelated` ignores N:N collections: it only creates 1:N children.

### Read a record with related records (`RetrieveWithRelated`)

`RetrieveWithRelated` (in `Vela.Context`) reads one record and its related records in one call, and fills the
navigation properties. The target can be an id or an alternate-key reference:

```csharp
using Vela.Context;

Account account = ctx.Service.RetrieveWithRelated<Account>(
    new EntityReference(Account.EntityLogicalNameValue, accountId),
    q => q.Include(a => a.PrimaryContact)     // N:1 parent, typed
          .Include(a => a.Contacts),          // 1:N children, typed
    a => a.Name);                             // root columns (optional; all columns when omitted)

string primaryContact = account.PrimaryContact?.FullName;
int contactCount = account.Contacts.Count;
```

Each `Include` reads all the related columns unless you pass column selectors, for example
`q.Include(a => a.Contacts, c => c.FullName)`. Including the same relationship twice throws. `Include` also works
for N:N relationships.

This is a **single-record** read, built on a `Retrieve` with related entities, so it reads one level deep and
each related set is limited to one page of 5,000 rows (see
[RetrieveRequest.RelatedEntitiesQuery](https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.messages.retrieverequest.relatedentitiesquery)).
Use it for bounded relationships. For a set of records or a deeper graph, query the children separately and match
them on the parent lookup.

### Associate N:N records (`AssociateRelated`)

An N:N link connects records that already exist. Populate the N:N collection with existing records (each with
its id) and call `ctx.Service.AssociateRelated(account)`, which associates every populated N:N collection. A
related record without an id throws, naming each one, before anything is associated.

### Intersect (N:N join) tables (`--intersect`)

An N:N relationship is stored in a hidden **intersect table** (for example `listmember`). To query it with a typed
query you need its model, but its logical name is rarely known. Add `--intersect` and `vela models` finds the
intersect tables of the scoped tables from their N:N relationship metadata and generates them too:

```bash
vela models --solution ZavaSales --intersect --namespace Zava.Shared.Models.Generated
```

It is opt-in and goes **one level** deep (the intersect tables of the scoped tables, not of those). It removes
duplicates and reports how many tables it added. It does not add the table at the other end of each N:N; add those
to `--entities` if you want their navigation properties. It needs live metadata, so it cannot be combined with
`--from-solution`, `--verify`, `--forms` or `--tables`.

## Security roles (`--roles` / `--solution-roles`)

`--roles "Salesperson,Sales Manager"` (role names) or `--solution-roles` (every role in `--solution`) generates a
`Roles` class in the shared models namespace, next to the table models. Each role gets three members: its name,
its **root** role id, and its privileges as read from the environment. A system role (one with a
`roletemplateid`) gets a fourth, `XTemplateId`. A `--roles` name the environment does not have is reported as a
warning and skipped; the command fails only when none of the requested roles exist.

```csharp
public static class Roles
{
    public const string SalespersonName = "Salesperson";
    public static readonly Guid SalespersonId = new Guid("1111...");   // the root role id
    public static readonly (string Entity, string Level, string Privilege)[] Salesperson = new[] // its privileges
    {
        ("account", "BusinessUnit", "Read"),
    };

    public const string SystemAdministratorName = "System Administrator";
    public static readonly Guid SystemAdministratorId = new Guid("2222...");
    public static readonly Guid SystemAdministratorTemplateId = new Guid("6270..."); // its roletemplateid
    // ... its privileges
}
```

Two root roles with the same name are both generated, as `Roles.X` and `Roles.X_2` in root id order.

**Which id to compare.** `XId` is the same in every environment only when the role ships in a solution; a role
created by hand in each environment gets a different id in each. A system role's `roletemplateid` is the same
everywhere, so to recognize a system role (for example System Administrator) in any environment, compare
`role.roletemplateid` with `XTemplateId`. A custom role has no template id and no `XTemplateId`.

**Use it in tests.** Pass the name, id and grants to the [security model](security-model.md) so the harness seeds
the real root role id and privileges:

```csharp
var svc = new InMemoryOrganizationService()
    .WithCurrentUser(alice)
    .WithSecurity(s => s
        .BusinessUnit(salesBu)
        .Role(Roles.SalespersonName, Roles.SalespersonId, Roles.Salesperson)
        .User(alice, salesBu, Roles.SalespersonName));
```

For a system role, pass the template id after the id:
`Role(Roles.SystemAdministratorName, Roles.SystemAdministratorId, Roles.SystemAdministratorTemplateId, Roles.SystemAdministrator)`.
The harness writes it as `roletemplateid` on every business unit copy of the role.

> [!NOTE]
> The generated `Roles` class uses BCL types only (no test dependency), so it lives with the shared runtime models.
> Plugin code can use `Roles.XName` and `Roles.XId` at run time; tests pass the grants to the harness through the
> `Role(name, id, grants)` overload.

**Check role membership in plugin code.** `Roles.XId` is the **root** role id. Dataverse keeps one copy of each
role per business unit, and `systemuserroles.roleid` / `teamroles.roleid` hold the copy of the user's or team's
business unit, so they equal `Roles.XId` only in the root business unit. Every copy carries the root id in
`parentrootroleid`; match on that column to check a role in any business unit (see
[Role membership](security-model.md)). With `role` and its intersect tables generated
(`vela models --entities role,team --intersect` adds `SystemUserRoles`, `TeamRoles` and `TeamMembership`):

[!code-csharp[](../samples/Vela.DocSamples/RoleMembershipSample.cs#role-membership)]

This query checks direct assignment only. A role held through a team goes through `teamroles` and
`teammembership` (link `TeamRoles` on `RoleId`, then `TeamMembership` on `TeamId`).

## Custom API message classes (`--custom-apis`)

`--custom-apis` generates a typed `OrganizationRequest` / `OrganizationResponse` pair per Custom API, into a
`Messages/` subfolder:

```csharp
var response = service.Execute(new zava_RecalculateRequest { AccountId = id })
    .ToResponse<zava_RecalculateResponse>();
```

The classes keep the API's metadata so that the definition can be rebuilt from them: a required request
parameter (`isoptional = false`) carries `[Required]`, and a parameter with a table (`logicalentityname`) carries
`[LogicalEntityName("...")]`. Response properties are always present.

Each API also gets an **`On<Name>()` step extension** whose shape follows the API's metadata, so an invalid
registration does not compile:

- bound to the API's table when the API is bound and that table is generated in the same run (otherwise a generic
  `On<Name><TEntity>()`);
- async-only when the API's `allowedcustomprocessingsteptype` is AsyncOnly (only `.Async()` compiles);
- omitted for an API that allows no custom processing step, or a private API.

See [React with a plugin step](custom-apis.md#react-with-a-plugin-step) for an example.

**Scoping.** A list (`--custom-apis a,b,c`) generates exactly those APIs. With **no value**, `--custom-apis`
generates every Custom API in the environment, including Microsoft's (often hundreds of files), and prints a
warning. To leave out the typed Custom APIs you wrote in code, pass your built package (`--package <file.nupkg>` or
`-p`, repeatable; `--packages-dir <folder>`; `--package-assembly <name.dll>` to pick the assembly): their unique
names are read from the package with the same offline scan `plan` and `apply` use, and removed. Only typed Custom
APIs (`CustomApiBase<TRequest, TResponse>`) are removed, because their classes are already in your code. A
lightweight Custom API (`CustomApiBase` with `RegisterCustomApi(name, handler)`) has no request class, so it is
still generated. The command reports how many APIs it removed and kept.

## SDK message classes (`--messages`)

`--messages a,b,c` generates typed `Request` / `Response` classes for SDK messages, into `Messages/`. With **no
value**, it generates every message you can register a step on (Microsoft's "available events": non-private
messages with a visible `sdkmessagefilter` that allows custom steps), except the messages backed by a Custom API,
which belong to `--custom-apis`.

**Use the request name.** The names are the SDK request names that Dataverse executes: `--messages SendEmail`
generates `SendEmailRequest` and `SendEmailResponse`. A request name is not always the name of the `sdkmessage` a
step registers on: `Close` serves `CloseIncident` and `CloseQuote`, `Send` serves `SendEmail`, `SendFax` and
`SendTemplate`, and each request has its own fields and table. An `sdkmessage` name that is not itself a request
(`--messages Send`) is rejected with the request names it serves. With no value, one class is generated per
request.

**SDK classes are reused.** For a request that already has a class in `Microsoft.Xrm.Sdk.Messages` or
`Microsoft.Crm.Sdk.Messages` (`CloseQuote`, `SetState`, `CalculateRollupField`, ...), `--messages` generates no
duplicate class, only the `On<Message>()` extension over the SDK type (`OnCloseQuote()` calls
`On<Microsoft.Crm.Sdk.Messages.CloseQuoteRequest>()` and compiles on `Quote` only). A message Vela already
curates (`Create`, `QualifyLead`, `WinOpportunity`, ...) generates nothing. The Shared project needs no new
reference: it already compiles against `Microsoft.CrmSdk.CoreAssemblies` (net48) and the Dataverse client
(net10.0).

**Step extensions.** Each generated message gets an `On<Message>()` extension per table its `sdkmessagefilter`
targets (`primaryobjecttypecode`), so it only compiles inside the `EntityPluginBase<T>` of those tables, like the
curated `On<Message>()` methods. A global message (`primaryobjecttypecode` `none`), or one that applies to more
than eight tables, gets a single generic `On<Message><TEntity>()` instead. The request classes also work with
`On<TRequest>()` directly and with `PluginContext.ForRequest(...)` in tests. The framework knows the `sdkmessage`
of each platform request (`On<CloseQuoteRequest>()` registers on `Close`); a request it does not know is marked
`[SdkMessage("...")]` (from `Vela.Messages`) so the step and the test harness use the right `sdkmessage`.

### Standard messages need no generation (`QualifyLead`)

Many standard Sales and business messages are **curated by Vela**, so their typed step method and context exist
without a `vela models` run. `QualifyLead` is one: register a step on it directly, and the handler receives the
typed `QualifyLeadRequest` and `QualifyLeadResponse`.

```csharp
public sealed partial class RouteQualifiedLead(string unsecureConfig, string secureConfig)
    : EntityPluginBase<Lead>(unsecureConfig, secureConfig)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnQualifyLead().PostOperation().Sync().Handle(OnQualified);

    private void OnQualified(OnQualifiedContext ctx)
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

To call the message from your own code, use the SDK request:

```csharp
var response = (QualifyLeadResponse)service.Execute(new QualifyLeadRequest
{
    LeadId = new EntityReference(Lead.EntityLogicalNameValue, leadId),
    CreateAccount = true,
    Status = new OptionSetValue(3), // Qualified
});
```

Use `--messages` only for a message Vela does **not** curate. The curated list is in
[Write a plugin](plugins.md#messages).

### Where the message files go

`--custom-apis` and `--messages` write two files per message into `Messages/`:

- `Messages/<Name>.cs`: the request/response contract, compiled by the Shared project;
- `Messages/<Name>.Steps.cs`: its `On<Message>()` step extension (an `internal` class), compiled by each plugin
  project.

The extension extends the `Vela.Plugins` step builder, which the Shared project does not reference. A solution
scaffolded by `vela init` already excludes `*.Steps.cs` from Shared and links it into the plugin projects. A
project scaffolded by an older `vela init` needs these two item groups added by hand:

```xml
<!-- Shared .csproj -->
<ItemGroup>
  <Compile Remove="Models\Generated\Messages\*.Steps.cs" />
</ItemGroup>

<!-- each plugin .csproj -->
<ItemGroup>
  <Compile Include="..\Acme.Shared\Models\Generated\Messages\*.Steps.cs" Link="Models\Generated\Messages\%(Filename)%(Extension)" />
</ItemGroup>
```

## Verify drift against the live schema (`--verify`)

The generated model is checked by the compiler against itself, not against the environment. If a column is
deleted or renamed in Dataverse and the model is not regenerated, the code still compiles, and a typed read
(including a typed image) returns `null` at run time. `--verify` finds this drift without writing anything:

```bash
vela models --verify --entities account,contact --url https://zava.crm.dynamics.com
```

It generates the scoped table classes from live metadata in memory and compares them, column by column, with the
`.cs` files under the output folder (resolved as for generation: `--out`, or the project's `Models/Generated`).
For each table it reports:

- **removed in Dataverse**: a column the local model has and the environment no longer has (the run-time `null`
  case);
- **type changed**: a column whose generated type differs (for example `int?` to `decimal?`);
- **added in Dataverse**: a column the environment has and the local model does not (regenerate to include it);
- a table with **no local file**.

**Roles.** With `--roles` or `--solution-roles`, `--verify` also compares the `Roles` class on disk with the roles
of the environment you connect to, for example a target environment before you deploy:

```bash
vela models --verify --roles "Salesperson,Sales Manager" --url https://test.crm.dynamics.com
vela models --verify --solution ZavaSales --solution-roles --url https://test.crm.dynamics.com
```

Role names match regardless of case. It reports:

- a role **not found in the environment**: a requested name the environment does not have, or a role the local
  class holds that the scope no longer returns (with `--solution-roles`, a role no longer in the solution);
- a role **not in the local `Roles` class** (regenerate to include it), or no `Roles` class on disk at all;
- a different **root id** (`Roles.XId`) or **template id** (`Roles.XTemplateId`, including one side having it and
  the other not);
- for each table and privilege: a grant only the local class has, a grant only the environment has, and a
  different **access level** (for example `User` locally, `Organization` live).

Roles that share a name are matched on their root id. When several roles of one name differ on both sides, they
cannot be paired, so each is reported as not found on the other side; regenerating clears it.

**Scope and exit code.** `--solution` also scopes tables, so `--solution ZavaSales --solution-roles` checks the
tables and the roles (only the roles when the solution holds no table); `--roles` alone checks only the roles.
`--custom-apis` and `--messages` are ignored, and a `--verify` with only those scopes is refused as invalid
arguments. `--verify` cannot be combined with `--intersect`, `--from-solution`, `--forms` or `--tables`.

`--verify` exits `5` when a table or role is out of sync and `0` when everything matches, so it can fail a CI
build on a stale model (see [Exit codes](../reference/machine-readable-output.md#exit-codes)). Add `--json` for a
machine-readable report: one entry per table under `tables` and, with a role scope, one entry per role under
`roles`, plus `missingRolesClass`.

## Offline generation from an exported solution (`--from-solution`)

`--from-solution` generates the table classes and choice enums with no Dataverse connection, from the
`customizations.xml` inside an exported unmanaged solution zip. Use it when a connection is not available yet,
for example while onboarding or migrating a project.

```bash
# Export the solution once (any tool), then generate offline.
vela models --from-solution ./MySolution.zip --namespace Acme.Shared.Models.Generated --out src/Acme.Shared/Models/Generated

# Only some tables of the zip.
vela models --from-solution ./MySolution.zip --entities adc_widget,adc_part --namespace Acme.Shared.Models.Generated
```

The offline path feeds the same generator as the connected path, so the classes have the same shape. It generates
the tables whose columns the export carries; a system table exported without columns is skipped. An export
carries less than live metadata, so some details are simplified rather than failing:

- a column of a type the generator does not map is skipped;
- a lookup has no `[LookupTargets]` (the export stores numeric type codes, not logical names) and stays a plain
  `EntityReference`;
- a global choice missing from the file falls back to `OptionSetValue`;
- no relationship navigation properties are generated.

It generates table classes only: `--forms`, `--tables`, `--verify` and `--intersect` need live metadata and are
refused, and the message and role scopes are not read. When the environment is reachable, prefer the connected
path, which has the full metadata.

## TypeScript models (preview)

> [!WARNING]
> Preview, work in progress. The TypeScript output is part of the [client-side](client-side.md) preview and may
> change or be removed in any release.

`--forms` and `--tables` generate TypeScript instead of C#, for the `vela-client` runtime. They need a table scope
(`--solution` and/or `--entities`), use the same connection flags, and accept only `--lang ts` (the default for
them). They ignore the message and role scopes, and cannot be combined with `--verify` or `--intersect`.
Generation is deterministic and all-or-nothing.

### Form models (`--forms`)

`--forms` generates a **`FormModel`** for each published **Main** and **Quick Create** form of the scoped tables.
It reads each form's FormXML together with the table metadata, so the model holds only the fields, tabs, sections
and subgrids on that form, each typed by the same rules as the C# models.

```powershell
vela models --forms --entities account,contact --out src/generated --interactive

# Machine-readable report of the generated files
vela models --forms --solution ZavaSales --out src/generated --json
```

Each form becomes a `forms/<entity>/<form>.form.ts` constant that `satisfies FormModel` (the folder is the table
logical name; the file name drops the table prefix). Choice fields reference a shared `option-sets/` folder at the
output root: `option-sets/global.ts` for global choices and `option-sets/<entity>.ts` for a table's local
choices. Fields follow the form layout order. Only published forms are read, and same-named forms of one table get
distinct file names. The FormXML parser accepts attributes newer than the published `FormXml.xsd`, so current forms
generate without errors.

### Table types (`--tables`)

`--tables` generates a **TypeScript record interface** per table, so `vela-client` Web API queries are typed without
writing the row type by hand. Each table becomes `tables/<logicalname>.ts`:

```powershell
vela models --tables --entities account,contact --out src/generated --interactive
```

Each file holds `export interface <Table> extends DataverseRecordMeta { ... }`, importing the same shared
`option-sets/` folder as `--forms`. Columns map by type:

| Column | TypeScript |
| --- | --- |
| String, memo | `string` |
| Integer, decimal, money and the other numeric types | `number` |
| Boolean | `boolean` |
| Date and time | `string` (the Web API returns ISO strings) |
| Choice / multi-select choice | its enum / an array of its enum |
| File, image | `string` |
| Lookup | its id as `_<name>_value` (for example `_primarycontactid_value?: string \| null`) |

Every column except the primary key is optional and nullable (`name?: string | null`); the primary key is required
(`accountid: string`). A **multi-target** lookup (Customer, Owner, or any lookup with more than one target) also
has a typed annotation naming the target table
(`'_<name>_value@Microsoft.Dynamics.CRM.lookuplogicalname'?: 'account' | 'contact'`).

Each interface has a runtime descriptor of the same name (imported from `vela-client/webapi`):

```ts
export const Account = defineTable<Account>({
  entity: 'account',
  primaryId: 'accountid',
  multiSelect: ['new_servicetypes'],
});
```

`multiSelect` is omitted when the table has no multi-select column. Columns follow metadata order, primary key
first.

**Activity tables** (email, appointment, ...) expose their party lists (`to`, `from`, `cc`,
`requiredattendees`, ...) as one `activityparty` collection told apart by `participationtypemask`. The record has
a single `<entity>_activity_parties?: ActivityParty[] | null` member instead of the party-list columns, and
`defineTable` adds `activityParties: '<entity>_activity_parties'`. Read them with `expand`, then filter by mask:

```ts
const { entities } = await createWebApi()
  .retrieveMultiple(Email)
  .select('subject')
  .expand<ActivityParty[]>('email_activity_parties', ['participationtypemask', '_partyid_value'])
  .execute();

const to = entities[0]?.email_activity_parties
  ?.filter((p) => p.participationtypemask === ParticipationType.ToRecipient);
```

### Root barrel (`index.ts`)

Both TypeScript scopes also write a root `<out>/index.ts` that re-exports every generated symbol by name (form
models, table interfaces and their descriptors, choice enums), so code imports from one place:

```ts
import { AccountMain, Contact, account_customertypecode } from './generated';
```

The re-exports are named (never `export *`), so the bundler can drop unused ones, and each name is exported once.
The barrel covers what the run generated and is written only when there is at least one symbol. Combine
`--forms` and `--tables` in one run to get one `option-sets/` folder and one barrel that covers both; a run with
only one of them writes a barrel for that scope only:

```powershell
vela models --forms --tables --entities account,contact --out src/generated --overwrite
```

## Not generated

`vela models` does not generate an `OrganizationServiceContext` or LINQ provider; use `pac modelbuilder` if you
need one. See the [Limitations reference](../reference/limitations.md).

## See also

- [Write a plugin](plugins.md): steps, images and the typed context that use these classes.
- [Typed queries](queries.md): `Query.For<T>()` over the generated classes.
- [Write a Custom API](custom-apis.md): author, call and react to a Custom API.
- [Security model](security-model.md): test with the generated `Roles` class.
