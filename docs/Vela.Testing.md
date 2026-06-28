<img src="../branding/png/vela-chevron.svg.png" width="64" align="left" alt="Vela" />

# Vela.Testing

**Unit-test Dataverse plugins and Custom APIs — no Dataverse connection, no mocking library.**

<br clear="left" />

`Vela.Testing` builds a fake plugin execution context and a stateful in-memory CRM so you can run your
plugins/Custom APIs in a plain test, assert on the records they produce, and even simulate the pipeline
(cascading plugins). It has **no dependency on any mocking framework**; you can still inject your own
`IOrganizationService` when you want.

```powershell
dotnet add package Vela.Testing
```

---

## 1. Run a plugin against a fake context

The fluent builder mirrors the registration DSL and is strongly typed. The trigger (message/stage/mode) is
**auto-derived** from the plugin's own registration, so you don't restate it.

```csharp
using Vela.Constants;
using Vela.StepBuilder;
using Vela.Testing;

// Build + run in one chain; returns the plugin instance for assertions.
AccountPostUpdate plugin = PluginContext.For<Account>()
    .WithTarget(new Account { Id = accountId, Name = "Zava" })
    .WithPreImage(new Account { Name = "Old name" })
    .Execute<AccountPostUpdate>();

// ...or build a LocalPluginContext for direct assertions:
LocalPluginContext ctx = PluginContext.For<Account>()
    .OnUpdate().Stage(PluginStages.PostOperation).Mode(ExecutionMode.Synchronous)
    .WithTarget(new Account { Name = "Zava" })
    .Build();
```

**Custom APIs / message-only plugins:** `PluginContext.ForMessage("vela_DoThing").WithInputParameter("amount", 5)`.

The builder also exposes: `WithInputParameter`/`WithOutputParameter`, `WithSharedVariable`, `WithUserId`,
`WithTargets(...)` (bulk), `WithManagedIdentityService`, `WithEnvironmentId`/`WithTenantId`/`AsApplicationUser`,
and `TraceCapture` (a `RecordingTracingService` to assert on emitted traces). `FakePluginExecutionContext`
implements the full `IPluginExecutionContext2..7` chain, so plugins reading newer members work.

---

## 2. Test doubles (no Moq)

| Type | Purpose |
| --- | --- |
| `FakePluginExecutionContext` | mutable execution context (full interface chain) |
| `FakeOrganizationService` | delegate hooks `OnCreate`/`OnRetrieve`/… (no-op by default) |
| `FakeOrganizationServiceFactory` | hands out user/system services |
| `RecordingTracingService` | captures `ctx.Trace(...)` for assertions |
| `InMemoryOrganizationService` | **stateful** CRM (see below) |

Inject any service yourself with `.WithOrganizationService(...)` — Moq, your own fake, etc.

---

## 3. Stateful in-memory CRM

`InMemoryOrganizationService` stores records so Create/Retrieve/Update/Delete round-trips just work.

```csharp
var crm = new InMemoryOrganizationService()
    .WithAlternateKey("account", "accountnumber")      // duplicate key -> throws
    .WithAutoNumber("task", "ticketnumber", "T-", 3)    // auto-populated on create: T-001, T-002, …
    .WithCurrentUser(currentUserId);                    // for ConditionOperator.EqualUserId

var id = crm.Create(new Entity("account") { ["name"] = "Zava" });
crm.Update(new Entity("account") { Id = id, ["name"] = "Renamed" });
Entity stored = crm.Retrieve("account", id, new ColumnSet(true));
```

- **Relationships:** for 1:N, set the lookup via `Update` (`account.primarycontactid = new EntityReference(...)`).
  For N:N, declare the relationship once with `WithManyToMany(...)` and use `Associate`/`Disassociate` — they
  create/delete the intersect rows, so `RetrieveMultiple` joins through them like Dataverse:

  ```csharp
  var crm = new InMemoryOrganizationService()
      .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

  crm.Associate("account", accountId, new Relationship("account_contacts"),
      new EntityReferenceCollection { new EntityReference("contact", contactId) });

  // The account's contacts, joined through the intersect entity:
  var q = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
  var link = q.AddLink("account_contact", "contactid", "contactid", JoinOperator.Inner);
  link.LinkCriteria.AddCondition("accountid", ConditionOperator.Equal, accountId);
  crm.RetrieveMultiple(q);
  ```

  `Associate` is idempotent (an existing pair is not duplicated). An undeclared relationship throws.

  With early-bound types there is a strongly-typed overload — the table and column names come from the types
  and the property accesses:

  ```csharp
  crm.WithManyToMany<AccountContact, Account, Contact>("account_contacts", ac => ac.AccountId, ac => ac.ContactId);
  ```

  `WithManyToMany(...)` is **optional** when the relationship is carried by registered metadata — declare it on a
  `FakeEntityMetadata` via `.ManyToMany(schema, intersect, e1, e1Attr, e2, e2Attr)` and `Associate`/`Disassociate`
  derive the intersect mapping from it.
- **Bulk & batch:** `Execute(...)` handles `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple` requests,
  `CreateMultipleRequest`/`UpdateMultipleRequest`, and `ExecuteMultipleRequest`/`ExecuteTransactionRequest`
  (ExecuteMultiple honours `ContinueOnError`/`ReturnResponses`; ExecuteTransaction rolls the whole batch back if any
  request throws).
- **File / image columns:** the block protocol works over an in-memory blob store — `InitializeFileBlocksUpload`
  → `UploadBlock` → `CommitFileBlocksUpload` (returns a `FileId`; the File column then stores that fileid),
  `InitializeFileBlocksDownload` → `DownloadBlock` (round-trips the content), and `DeleteFile`. These messages
  have no strongly-typed request class in the referenced SDK, so call them with a plain
  `OrganizationRequest("InitializeFileBlocksUpload") { Parameters = { … } }`. An Image column that holds bytes
  inline is downloaded directly (its bytes are the thumbnail); a plain `byte[]`/fileid attribute already
  round-trips through Create/Update/Retrieve.

### Standard system columns

Create/Update auto-populate the standard columns the platform sets for you, so plugins that read or filter
on them behave like they do online:

- **Audit:** `createdon`/`createdby` on Create, `modifiedon`/`modifiedby` on Create and every Update,
  stamped with the calling user (`WithCurrentUser(...)`). When unset, a stable non-empty default user
  (`InMemoryOrganizationService.DefaultCallingUser`) is used instead of the empty GUID.
- **Owner (user/team-owned tables):** `ownerid`, `owninguser` *or* `owningteam`, and `owningbusinessunit`
  (from `WithCurrentBusinessUnit(...)`). A caller-supplied `ownerid` is honoured and the owning columns are
  derived from it. Assign by `Update`-ing `ownerid` to a `team` reference (`owningteam` follows) or via
  `Execute(new AssignRequest { Target = ..., Assignee = new EntityReference("team", id) })`.
- **Organization (org-owned tables):** `organizationid` (from `WithOrganizationId(...)`), no owner columns.
- **State:** `statecode` (0) and `statuscode` (1) default on Create so plugins that read them see an active
  record; a caller-supplied value is honoured.
- **Row version:** `versionnumber` is stamped on Create and bumped on every Update (a monotonic counter).
- **Backdating:** a caller-supplied `overriddencreatedon` sets `createdon` to that value on Create, like a
  data migration online.
- **Timestamps** use `DateTime.UtcNow` by default; pin them with `WithClock(() => fixedInstant)` to assert
  `createdon`/`modifiedon` deterministically.
- **Ids** are random `Guid`s by default; make them reproducible with `WithIdSequence()` (deterministic
  `00000000-0000-0000-0000-000000000001`, `...0002`, … — assert with `SequentialIds.At(n)`) or
  `WithIdSequence(() => myGuid)` for a custom generator. Virtual tables get none of these system columns.

The **ownership type** is resolved per table: an early-bound row is inferred (a generated class exposes an
`OwnerId` property only for user/team-owned tables); late-bound rows default to user-owned. Declare it
explicitly to override inference or to model the special table types:

```csharp
var crm = new InMemoryOrganizationService()
    .WithCurrentUser(userId)
    .WithCurrentBusinessUnit(businessUnitId)
    .WithOrganizationId(organizationId)
    .AsOrganizationOwned("savedquery")  // audit + organizationid, no owner columns
    .AsVirtual("zava_externaldata")     // virtual table: no system columns at all
    .AsVirtual("zava_readonly", readOnly: true)  // read-only external provider: Create/Update/Delete are rejected
    .AsElastic("zava_telemetry");       // elastic is a storage type, not ownership: audit + owner columns
```

### Table metadata

Plugins that read metadata (`RetrieveEntityRequest`, `RetrieveAllEntitiesRequest`, `RetrieveAttributeRequest`)
work once you register the tables you need. Build the metadata with `FakeEntityMetadata` (it handles the SDK's
read-only metadata setters for you):

```csharp
var crm = new InMemoryOrganizationService()
    .WithEntityMetadata(
        FakeEntityMetadata.For("account")
            .PrimaryId("accountid")
            .PrimaryName("name")
            .Ownership(OwnershipTypes.UserOwned)
            .Attribute("name", AttributeTypeCode.String)
            .Picklist("statuscode", (1, "Active"), (2, "Inactive"))
            .Build());

var response = (RetrieveEntityResponse)crm.Execute(
    new RetrieveEntityRequest { LogicalName = "account", EntityFilters = EntityFilters.All });
EntityMetadata account = response.EntityMetadata; // PrimaryIdAttribute, Attributes, OwnershipType, …
```

Only what you register is returned; an unregistered table throws.

### Queries

`RetrieveMultiple` runs `QueryExpression`, `QueryByAttribute` and `FetchExpression` (FetchXML, common subset):

```csharp
var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
link.EntityAlias = "acct";
link.Columns = new ColumnSet("name");
query.Criteria.AddCondition("fullname", ConditionOperator.BeginsWith, "J");

Entity contact = crm.RetrieveMultiple(query).Entities.Single();
contact.GetAttributeValue<AliasedValue>("acct.name").Value; // "Zava"
```

Supported: filters (And/Or, operators incl. Between, Begins/Ends/Like, date operators, EqualUserId,
CompareColumns), conditions on linked entities (records-not-in-a-set), link-entities
(Inner/LeftOuter/Exists/In/MatchFirstRowUsingCrossApply, aliased columns, criteria, nested), Any/All filters,
ordering (incl. linked columns), TopCount, paging, Distinct, total record count, column aliases, and
aggregates (count/sum/avg/min/max + group-by). FetchXML aggregates via `<fetch aggregate='true'>`.

### Typed query builder

`Query.For<T>()` produces the same `QueryExpression`, fully typed, no repeated type arguments:

```csharp
QueryExpression q = Query.For<Contact>()
    .Select(c => c.FullName)
    .Where(c => c.FullName, ConditionOperator.BeginsWith, "J")
    .Link<Account>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.Inner, "acct",
        acct => acct.Select(a => a.Name))
    .OrderBy(c => c.FullName);

crm.RetrieveMultiple(q); // implicit conversion to QueryExpression
```

Also: `Distinct()`, `Page(n, count)`, `WithTotalRecordCount()`, `WhereColumns(a, op, b)`,
`WhereLink<TLinked>(alias, …)`, `WhereRelated<TTo>(…, JoinOperator.Any/All/…)`, and aggregates
`GroupBy/Count/Sum/Average/Min/Max`.

---

## 4. Pipeline simulation (cascade)

`PluginPipeline` simulates the Dataverse pipeline in memory: writes auto-fire the registered plugins and
**cascade** (a plugin that writes triggers more plugins). Registrations are discovered from the assembly.

```csharp
var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>()
    .WithAutoNumber("task", "ticketnumber", "T-", 3);

pipeline.Service.Create(new Account { Name = "Zava" });   // fires Create-account plugins, then cascades

Assert.Single(pipeline.GetAll("task"));
Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountPostCreate"));
```

- Pre/post stages, execution order, filtering attributes, pre/post images.
- Synchronous-plugin exceptions **roll the operation back**; **async** steps run inline after commit.
- **Custom APIs** via `Service.Execute(new OrganizationRequest("vela_MyApi"))`.
- **Bulk (`CreateMultiple`/`UpdateMultiple`)** fire steps the way Dataverse merges the two messages: a
  `CreateMultiple`/`UpdateMultiple` request runs your single `Create`/`Update` steps **once per row**, and a
  step registered with `OnCreateMultiple()`/`OnUpdateMultiple()` runs **once with the whole batch** (read it via
  `ctx.BulkTargets`, with per-row pre/post images on `ctx.BulkPre/PostEntityImagesCollection`). The merge runs
  both ways, so a bulk step also fires for a plain `Create`/`Update` (a one-row batch). The whole batch is one
  transaction — a synchronous failure rolls every row back.
- **Owner/state Update split & `SetState`:** an `Update` that carries `ownerid` or `statecode`/`statuscode`
  **alongside** standard fields fires the Update step **twice** — once for the standard fields, then once for the
  owner/state fields — like the platform. `Execute(new SetStateRequest { … })` is mapped to a
  `statecode`/`statuscode` Update (so Update plugins run; the status defaults from the state when omitted),
  mirroring how `AssignRequest` is modelled as an `ownerid` update.
- **Bypass parameters** on an `Execute(...)` request suppress steps the way the platform does:
  `BypassCustomPluginExecution = true` skips every step; `BypassBusinessLogicExecution = "CustomSync"` /
  `"CustomAsync"` skips by execution mode; and `BypassBusinessLogicExecutionStepIds` skips specific steps
  (matched against the step **key** — pass a comma-separated string or a collection of keys). The data is still
  written; only the plugins are skipped.
- `PluginPipeline.Empty().Register<MyPlugin>()` to scope a test to specific plugins.

---

## Not supported (by design)

Query hints, NoLock, `FilterHint=union`, faithful paging cookies. `Associate`/`Disassociate` persist intersect
rows once the relationship is declared (`WithManyToMany(...)` or registered metadata), but a 1:N relationship is
modelled by `Update`-ing the lookup column. For anything else, inject your own `IOrganizationService`.

For the full picture across the whole framework (runtime, CLI and this test harness), see
[`LIMITATIONS.md`](LIMITATIONS.md).
