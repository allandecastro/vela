# Testing with Vela.Testing

`Vela.Testing` builds a fake plugin execution context and a stateful in-memory CRM, so you can unit-test
plugins and Custom APIs **without a Dataverse connection** and **without any mocking library**. The fluent
builder mirrors the registration DSL (`OnUpdate().Stage(...).Sync()`), and the primary entity name is
derived from `[EntityLogicalName]`. You can still inject your own `IOrganizationService` when you need
behaviour the built-in fakes don't cover.

```powershell
dotnet add package Vela.Testing
```

## Run a plugin against a fake context

The fluent builder is strongly typed. The trigger (message/stage/mode) is **auto-derived** from the plugin's
own registration, so you don't restate it.

```csharp
using Vela.Constants;
using Vela.Steps;
using Vela.Testing;

// Construct the plugin, run it, then assert on the instance.
AccountPostUpdate plugin = new AccountPostUpdate(unsecure: "", secure: "");
PluginContext.For<Account>()
    .WithTarget(new Account { Id = accountId, Name = "Zava" })
    .WithPreImage(new Account { Name = "Old name" })
    .Execute(plugin);

// ...or build a LocalPluginContext for direct assertions (restate the trigger to override the default):
LocalPluginContext ctx = PluginContext.For<Account>()
    .OnUpdate().Stage(PluginStages.PostOperation).Sync()
    .WithTarget(new Account { Name = "Zava" })
    .Build();
```

`Build<MyPlugin>()` (or `Build(plugin)`) derives the message/stage/mode from the plugin's single registered
step when you do not set one explicitly, the same inference `Execute` applies - so Build and Execute stay
consistent. The plugin-less `Build()` keeps whatever trigger you set (or the default).

You can also let the harness construct the plugin for you with `.Execute<AccountPostUpdate>()` (it returns the
instance for assertions). This works whether the plugin has a parameterless constructor or the Vela
`(string unsecureConfig, string secureConfig)` primary constructor that `vela init`/`vela add` generate.

**Custom APIs / message-only plugins:** `PluginContext.ForMessage("vela_DoThing").WithInputParameter("amount", 5)`.

The builder also exposes: `WithInputParameter`/`WithOutputParameter`, `WithSharedVariable`, `WithTag`,
`WithUserId`, `WithTargets(...)` (bulk `CreateMultiple`/`UpdateMultiple`), `WithManagedIdentityService`,
`WithEnvironmentId`/`WithTenantId`/`AsApplicationUser`, and `TraceCapture` (a `RecordingTracingService` to
assert on emitted traces). `FakePluginExecutionContext` implements the full `IPluginExecutionContext2..7`
chain, so plugins reading newer context members work.

## Test doubles (no Moq)

| Type | Purpose |
| --- | --- |
| `FakePluginExecutionContext` | mutable execution context (full interface chain) |
| `HookOrganizationService` | delegate hooks `OnCreate`/`OnRetrieve`/... (no-op by default) |
| `HookOrganizationServiceFactory` | hands out user/system services |
| `RecordingTracingService` | captures `ctx.Trace(...)` for assertions |
| `InMemoryOrganizationService` | **stateful** CRM (see below) |

Inject any service yourself with `.WithOrganizationService(...)`: Moq, your own fake, etc.

## Stateful in-memory CRM

`InMemoryOrganizationService` stores records so Create/Retrieve/Update/Delete round-trips just work.

```csharp
var crm = new InMemoryOrganizationService()
    .WithAlternateKey("account", "accountnumber")      // duplicate key -> throws
    .WithAutoNumber("task", "ticketnumber", "T-", 3)    // auto-populated on create: T-001, T-002, ...
    .WithCurrentUser(currentUserId);                    // for ConditionOperator.EqualUserId

var id = crm.Create(new Entity("account") { ["name"] = "Zava" });
crm.Update(new Entity("account") { Id = id, ["name"] = "Renamed" });
Entity stored = crm.Retrieve("account", id, new ColumnSet(true));
```

To run a plugin against a stateful store, pass the service on the builder and assert on the resulting CRM
state:

```csharp
PluginContext.For<Account>()
    .WithTarget(new Account { Id = accountId, Name = "Zava" })
    .WithOrganizationService(crm)
    .Execute(new ProvisionAccountPlugin());

Assert.Single(crm.GetAll("task"));
```

### Relationships

For 1:N, set the lookup via `Seed`/`Update` (`account.primarycontactid = new EntityReference(...)`). A lookup
is just **data**: the child carries an `EntityReference` to its parent, and that alone drives queries,
`link-entity` joins and filtering. You only need to *declare* the relationship with
`WithParentalRelationship(...)` / `WithReferentialRelationship(...)` (see the
[security model](security-model.md)) when the test exercises a **cascade or inherited access**; a plain lookup
with no declared relationship is correct data, not a broken test.

For N:N, declare the relationship once with `WithManyToMany(...)` and use `Associate`/`Disassociate`: they
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

`Associate` is idempotent (an existing pair is not duplicated); an undeclared relationship throws. With
early-bound types there is a strongly-typed overload, where the table and column names come from the types and
the property accesses:

```csharp
crm.WithManyToMany<AccountContact, Account, Contact>("account_contacts", ac => ac.AccountId, ac => ac.ContactId);
```

`WithManyToMany(...)` is **optional** when the relationship is carried by registered metadata: declare it on a
`FakeEntityMetadata` via `.ManyToMany(schema, intersect, e1, e1Attr, e2, e2Attr)` and `Associate`/`Disassociate`
derive the intersect mapping from it.

### Bulk & batch

`Execute(...)` handles `Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple` requests,
`CreateMultipleRequest`/`UpdateMultipleRequest`, and `ExecuteMultipleRequest`/`ExecuteTransactionRequest`
(ExecuteMultiple honours `ContinueOnError`/`ReturnResponses`; ExecuteTransaction rolls the whole batch back if
any request throws).

### File / image columns

The block protocol works over an in-memory blob store: `InitializeFileBlocksUpload` -> `UploadBlock` ->
`CommitFileBlocksUpload` (returns a `FileId`; the File column then stores that fileid),
`InitializeFileBlocksDownload` -> `DownloadBlock` (round-trips the content), and `DeleteFile`. These messages
have no strongly-typed request class in the referenced SDK, so call them with a plain
`OrganizationRequest("InitializeFileBlocksUpload") { Parameters = { ... } }`. An Image column that holds bytes
inline is downloaded directly (its bytes are the thumbnail); a plain `byte[]`/fileid attribute already
round-trips through Create/Update/Retrieve.

### Standard system columns

Create/Update auto-populate the standard columns the platform sets for you, so plugins that read or filter on
them behave like they do online:

- **Audit:** `createdon`/`createdby` on Create, `modifiedon`/`modifiedby` on Create and every Update, stamped
  with the calling user (`WithCurrentUser(...)`). When unset, a stable non-empty default user
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
  `00000000-0000-0000-0000-000000000001`, `...0002`, ...; assert with `SequentialIds.At(n)`) or
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
EntityMetadata account = response.EntityMetadata; // PrimaryIdAttribute, Attributes, OwnershipType, ...
```

Only what you register is returned; an unregistered table throws.

**Or derive it from the early-bound type**, with no hand-written metadata. `FakeEntityMetadata.From<TEntity>()`
reflects over a class generated by `vela models`: each `[AttributeLogicalName]` property becomes an attribute
(its CLR type maps to the `AttributeTypeCode`), generated option-set enums become picklists, the conventional
`{logicalname}id` column is the primary id, a `name` column is the primary name, ownership is inferred from an
`ownerid` column, and max length is recovered from the generated `[StringLength]` annotation.
`WithEntityMetadata<Account>()` is the one-liner shorthand:

```csharp
var crm = new InMemoryOrganizationService().WithEntityMetadata<Account>();   // zero metadata, zero connection
```

It returns the same builder, so chain overrides for the facets the early-bound type can't carry: select an
attribute with `On(...)` then set the facet:

```csharp
.WithEntityMetadata(
    FakeEntityMetadata.From<Account>()
        .On("name").MaxLength(160)
        .On("primarycontactid").Targets("contact")
        .Build());
```

Override-only (the early-bound type doesn't carry them; set with `On(...)` + a facet setter, or `For(...)`):
lookup targets, required level, numeric range, and any non-conventional primary-name column. Skipped entirely
(no faithful single mapping): multi-select choices, file/image columns, and partylists.

### Queries

`RetrieveMultiple` runs `QueryExpression`, `QueryByAttribute` and `FetchExpression` (FetchXML, common subset):

```csharp
var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
var link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
link.EntityAlias = "acct";
link.Columns = new ColumnSet("name");
query.Criteria.AddCondition("fullname", ConditionOperator.BeginsWith, "J");

Entity contact = crm.RetrieveMultiple(query).Entities.Single();
object name = contact.GetAttributeValue<AliasedValue>("acct.name").Value; // "Zava"
```

Supported: filters (And/Or, operators incl. Between, Begins/Ends/Like, date operators, EqualUserId,
CompareColumns), conditions on linked entities (records-not-in-a-set), link-entities
(Inner/LeftOuter/Exists/In/MatchFirstRowUsingCrossApply, aliased columns, criteria, nested), Any/All filters,
ordering (incl. linked columns), TopCount, paging, Distinct, total record count, column aliases, and
aggregates (count/countcolumn/sum/avg/min/max + group-by, also on linked columns and over joins).

FetchXML is parsed into the equivalent `QueryExpression` and runs through the same engine path, so a query and
its `ToFetchXml()` return the same rows. The parser is strict: anything it does not model throws
`NotSupportedException` naming the element or attribute, never silently ignored.

| FetchXML | Supported |
| --- | --- |
| `<fetch>` | `aggregate`, `distinct`, `top`, `count`, `page`, `returntotalrecordcount`, `aggregatelimit`; accepted as no-op hints: `no-lock`, `latematerialize`, `options`, `useraworderby`, `paging-cookie`, `mapping`, `version`, `output-format` |
| `<entity>` | `name`; children `attribute`, `all-attributes`, `filter`, `order`, `link-entity` |
| `<attribute>` | `name`, `alias`; in an aggregate: `aggregate` (count/countcolumn/sum/avg/min/max), `groupby`, `dategrouping` (day/month/quarter/year), `distinct` (countcolumn), `usertimezone` (the store is UTC) |
| `<link-entity>` | `name`, `from`, `to`, `alias`, `link-type` (inner, outer, exists, in, matchfirstrowusingcrossapply, and any/not any/all/not all, also inside a `<filter>`); `visible`/`intersect` accepted; children as `<entity>` |
| `<filter>` | `type` (and/or); `hint`/`isquickfindfields` accepted; children `condition`, `filter`, `link-entity` |
| `<condition>` | `attribute`, `operator`, `value` / `<value>`, `entityname` (a linked column, selected or not), `valueof` (column comparison: eq/ne/gt/ge/lt/le); `uiname`/`uitype`/`uihidden` accepted |
| `<order>` | `attribute`, `descending`, `entityname` (root only); `alias` in an aggregate |

Rejected: `datasource` (retained data), `rowaggregate`, `link-type='natural'`, week/fiscal-period/fiscal-year date
grouping, and every other element or attribute.

### Typed query builder

`Query.For<T>()` produces the same `QueryExpression`, fully typed, with no repeated type arguments:

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
`WhereLink<TLinked>(alias, ...)`, `WhereRelated<TTo>(..., JoinOperator.Any/All/...)`, and aggregates
`GroupBy/Count/Sum/Average/Min/Max`.

## Simulate the pipeline (cascade)

For scenario tests that go beyond a single plugin, `PluginPipeline` simulates the Dataverse pipeline **in
memory**: writes through its service auto-fire the registered plugins (pre/post stages, execution order,
filtering attributes, pre/post images), and a plugin that writes triggers further plugins (**cascade**).
Registrations are **discovered automatically** from the assembly via `GetStepDefinitions()`.

```csharp
var pipeline = PluginPipeline.FromAssemblyOf<AccountPostCreate>()
    .WithAutoNumber("task", "ticketnumber", "T-", 3);

pipeline.Service.Create(new Account { Name = "Zava" });   // fires Create-account plugins, then cascades

Assert.Single(pipeline.GetAll("task"));        // assert on the resulting CRM state
Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("AccountPostCreate")); // and on what ran
```

- Synchronous-plugin exceptions **roll the operation back** (snapshot/restore); **asynchronous** steps run
  inline after the synchronous commit. An asynchronous step's exception does **not** surface to the caller
  (matching Dataverse, where async steps are decoupled system jobs) - it is recorded on
  `pipeline.AsyncStepFailures` so you can still assert an async step failed. Cascade depth is guarded (the
  plugin's own depth guard plus a pipeline cap via `WithMaxDepth`).
- **Execution-context identity.** The plugin's `ctx` reports the configured caller: `UserId`,
  `InitiatingUserId`, `BusinessUnitId` and `OrganizationId` come from `WithCurrentUser(...)` /
  `WithCurrentBusinessUnit(...)` / `WithOrganizationId(...)`, consistent with what `WhoAmI` and the
  `createdby`/`ownerid` stamping report (not random values).
- **Step registration is honoured.** A `.Disabled()` step never fires. The plugin is constructed with the step's
  `WithUnsecureConfig(...)` value and, for `WithSecureConfigFrom("Name")`, the value you supply with
  `pipeline.WithSecureConfig("Name", "value")` (what `vela apply --secure-config Name=value` provides in
  production; unset configuration is passed as `null`). A step registered with `WithImpersonation(userId)` runs with
  `ctx.UserId` set to that user, and its `ctx.Service` acts as that user (privileges under `WithSecurity`, audit
  stamping); `InitiatingUserId` stays the caller.
- **User vs SYSTEM service.** `ctx.Service` acts as `ctx.UserId` and is subject to the security model when
  `WithSecurity` is used; `ctx.SystemService` is SYSTEM and is not (audit columns still record the calling user). A
  plugin fired by a write made through `ctx.SystemService` runs as SYSTEM: its own `ctx.Service` is unrestricted too.
- **`ParentContext` on a cascade.** A plugin fired by another plugin's write sees its caller's context as
  `ctx.PluginExecutionContext.ParentContext` (with its shared variables), so a re-entrancy guard like
  `if (ctx.PluginExecutionContext.ParentContext?.MessageName == "Create") return;` behaves as it does in Dataverse.
- **Not-found faults.** Retrieving or updating a record that does not exist throws
  `FaultException<OrganizationServiceFault>` with the platform `ObjectDoesNotExist` code (`-2147220969`), like a
  real `IOrganizationService`, so a plugin that catches that fault code is exercised faithfully.
- **Custom APIs** run through `Service.Execute(new OrganizationRequest("vela_MyApi"){ ["x"] = 1 })`.
- **Bulk (`CreateMultiple`/`UpdateMultiple`)** fire steps the way Dataverse merges the two messages: a
  `CreateMultiple`/`UpdateMultiple` request runs your single `Create`/`Update` steps **once per row**, and a
  step registered with `OnCreateMultiple()`/`OnUpdateMultiple()` runs **once with the whole batch** (read it
  via `ctx.BulkTargets`, with per-row pre/post images on `ctx.BulkPre/PostEntityImagesCollection`). The merge
  runs both ways, so a bulk step also fires for a plain `Create`/`Update` (a one-row batch). The whole batch is
  one transaction: a synchronous failure rolls every row back.
- **Owner/state Update split & `SetState`:** an `Update` that carries `ownerid` or `statecode`/`statuscode`
  **alongside** standard fields fires the Update step **twice**, once for the standard fields, then once for
  the owner/state fields, like the platform. `Execute(new SetStateRequest { ... })` is mapped to a
  `statecode`/`statuscode` Update (so Update plugins run; the status defaults from the state when omitted),
  mirroring how `AssignRequest` is modelled as an `ownerid` update.
- **Shared variables / `tag`.** A client passes the `tag` via a named request (not the shortcut methods, per
  the platform); the pipeline routes `Execute(CreateRequest/UpdateRequest/...)` through the registered steps
  and exposes it as a shared variable. Read it with `ctx.Tag` (or `ctx.GetSharedVariable("key")`):

  ```csharp
  // plugin: read the client tag
  private void OnAccountCreated(ICreatePostContext<Account> ctx)
  {
      if (ctx.Tag == "bulk-import") { /* skip enrichment for imports */ }
  }

  // test: pass it on the request
  var request = new CreateRequest { Target = new Account { Name = "Zava" } };
  request["tag"] = "bulk-import";
  pipeline.Service.Execute(request);
  ```

  For a single-plugin unit test, set it on the context builder instead:
  `PluginContext.For<Account>()....WithTag("bulk-import")`.
- **Bypass parameters** on an `Execute(...)` request suppress steps the way the platform does:
  `BypassCustomPluginExecution = true` skips every step; `BypassBusinessLogicExecution = "CustomSync"` /
  `"CustomAsync"` skips by execution mode; and `BypassBusinessLogicExecutionStepIds` skips specific steps
  (matched against the step **key**: pass a comma-separated string or a collection of keys). The data is still
  written; only the plugins are skipped. They apply at any depth: a request's effective bypass is the one it
  inherits (from an enclosing request or an `ExecuteMultiple`/`ExecuteTransaction` batch) plus its own, so a plugin
  that writes with `BypassCustomPluginExecution = true` does not re-enter itself.
- **Associate/Disassociate** fire their registered plugins whatever the entity (they are global messages, as on
  the platform: a step declared on `account` also fires for an association made from `contact`), and the N:N
  intersect rows persist when the relationship is declared with `WithManyToMany(...)` (or derived from registered
  metadata); the store has no live relationship metadata otherwise.
- **Upsert and alternate keys.** `Execute(new UpsertRequest { ... })` matches the target by id or alternate key
  (declared with `WithAlternateKey(...)`): an existing record runs the Update steps, a new one runs the Create steps,
  and the response reports `RecordCreated` and `Target`. `Update`/`Delete` addressed by `KeyAttributes` resolve the
  key first, so their steps see the real record id.
- Use `PluginPipeline.Empty().Register<MyPlugin>()` to scope a test to specific plugins.

## Not supported (by design)

Query hints, NoLock, `FilterHint=union`, faithful paging cookies. `Associate`/`Disassociate` persist intersect
rows once the relationship is declared (`WithManyToMany(...)` or registered metadata), but a 1:N relationship
is modelled by `Update`-ing the lookup column. For anything else, inject your own `IOrganizationService`.

The harness does **not** simulate the server-side **data effect** of non-CRUD "modelled" messages
(`QualifyLead`, `WinOpportunity`, `SendEmail`, `CloseIncident`, and the like): it fires the steps registered on
those messages so you can test your own logic, but the record changes the platform would make are not produced.
Like the platform, it fires the steps of the sdkmessage the request runs as (`Send` for a `SendEmailRequest`), on
the table the message runs on (queueitem for `AddToQueue`, whatever its `Target`), with an empty
`ctx.PrimaryEntityId`, so a step registered on the wrong message or table does not fire in a test either.
Only `PrincipalPrivilegeDenied` (security) and `ObjectDoesNotExist` (not-found) faults carry a real Dataverse
`ErrorCode`; other faults (for example an `ExecuteMultiple`/`ExecuteTransaction` child failure) surface an
`OrganizationServiceFault` with no `ErrorCode`. There is no formula/rollup/calculated-column, business-rule, or
duplicate-detection simulation.

For the full picture across the whole framework (runtime, CLI and this test harness), see
[Limitations](../reference/limitations.md).
