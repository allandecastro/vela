# Backlog

Planned and candidate work for Vela, grouped by area and rough priority. This is a living list: keep it in
sync by removing items once they ship (the [`CHANGELOG.md`](CHANGELOG.md) records what shipped) or reconsidering
them. For what is intentionally *out of scope*, see the [Limitations reference](https://allandecastro.github.io/vela/reference/limitations.html).

Priority legend: **P1** high value / clear need · **P2** worthwhile · **P3** nice-to-have polish.

## CLI

- **P3 - rare parallel-build pack race (needs a reliable repro first).** Once, the `VELA_BUILD_SMOKE` test's
  `restore --disable-parallel` then `build --no-restore` (parallel) sequence failed with `The process cannot
  access the file '...<Plugin>.1.0.0.nupkg' because it is being used by another process` - two plugin projects
  (`.Plugins` + `.Integrations`) `GeneratePackageOnBuild` into the shared `artifacts/` (`PackageOutputPath`).
  **Not reproducible so far** (10 clean runs, including the documented `dotnet build <sln> -c Release` *with*
  restore, which never raced - the flake was only on the test's `--no-restore` parallel path). The test is
  mitigated with `-m:1`. Because it can't be reproduced, any code fix would be unverifiable, so first capture an
  MSBuild binlog when it recurs to find the real cause (double-pack of one project vs. two writers), then fix
  (e.g. serialize the Pack target) rather than telling users to pass `-m:1`. User impact looks low: the
  documented command includes restore and did not race.
- **P3 - ribbon deploy end to end.** The classic `RibbonDiffXmlGenerator` (+ `RibbonSchema` validation) exists as
  a utility, but nothing wires the command bar automatically: registering a command on a button is left to the
  developer (Power Apps command designer, or a hand-authored `RibbonDiffXml`). Automating it would need a source
  of truth for the exported command functions (a build manifest from `vela-client`, or a scan) plus a Dataverse
  apply (modern `appaction` records / solution import). Deferred: low value versus letting the dev own the
  wiring. Design: `vela-client` `budapest/docs/plans/ribbon-command-bar.md` (PR-R4).

## Runtime (Vela.Plugins)

The message-dependent typed target redesign shipped as per-message, stage-split typed handler contexts
(`I<Message>PreContext<TEntity>` / `I<Message>PostContext<TEntity>` exposing `ctx.Request`/`ctx.Response`, with
reference / bulk targets read via `ctx.Request.Target` / `ctx.Request.Targets`); see the CHANGELOG. 46 messages
are modelled so far.

- **P1 - environment-driven message generation (early-bound style).** The typed message surface is hand-authored
  and curated in the framework today; the plan is to derive typed request/response from metadata (so the long tail
  of SDK messages and consumed Custom APIs get typed without a framework release), while keeping the curated core
  and the code-first Custom API declare-then-provision flow. Full design, roles (implement vs react) and phases:
  `.context/message-generation-plan.md`.

- **P3 - candidate typed messages to model next.** Superseded in spirit by the P1 generation item above (which
  covers the long tail from metadata); this list stays as the hand-add fallback for the curated core. All exist as
  `Microsoft.Crm.Sdk.Messages.*Request/*Response` (verified by reflection) and are business/registerable; add on
  demand via the recipe in `docs/contributing/extending-plugins.md`. Deferred because they are lower-frequency and
  the additive recipe makes each cheap. Skip the long tail of metadata/customization/admin/read-only messages (`Publish*`, `*Solution`,
  `Retrieve*Component`, licensing, provisioning, `Search*`/`Query*`): those stay on the permissive `OnMessage("...")`.
  - **Service / contracts:** `RenewContract`, `RenewEntitlement`.
  - **Marketing lists:** `QualifyMemberList` (the real "qualify member"; the singular `QualifyMember` does not exist
    in the SDK), `AddItemCampaign`, `RemoveItemCampaign`, `CopyCampaignResponse`.
  - **Process / automation:** `ExecuteWorkflow`, `SetProcess`, `Recalculate`.
  - **Pricing locks:** `LockSalesOrderPricing`, `UnlockSalesOrderPricing`, `LockInvoicePricing`, `UnlockInvoicePricing`.
  - **Scheduling:** `Reschedule`, `ImmediateBook`.
  - **Comms:** `SendTemplate`, `SendBulkMail`, `SendFax`.
  - **Ownership / records:** `ReassignObjectsOwner`, `InitializeFrom`.
  - **Product kits:** `AddProductToKit`, `RemoveProductFromKit`, `ConvertProductToKit`, `ConvertKitToProduct`.
  - **Deprecated - never model:** `SetState`, `SetStateDynamicEntity`, `Assign` (use an Update of
    `statecode`/`statuscode`/`ownerid`).

- **P3 - SQL query surface over the Web API SQL endpoint.** Dataverse exposes a read-only T-SQL subset through
  the Web API `sql` query option (`GET [Org]/api/data/v9.2/accounts?sql=SELECT name FROM account WHERE ...`),
  returning the same JSON as the equivalent OData/FetchXML query. Docs:
  https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/query/sql. Candidate for a typed
  query surface alongside the existing FetchXML builder (`Query.For<T>()` in `src/Vela.Plugins/Querying`), and/or
  a vela-client angle. Constraints to honour if we build it: read-only; a single `SELECT` per command; no
  `SELECT *` (name every column); `INNER`/`LEFT JOIN` only (no `RIGHT`/`FULL`/`CROSS`); `WHERE` limited to a
  boolean tree of column-vs-constant comparisons (`=`,`!=`,`<`,`>`,`<=`,`>=`,`LIKE`,`IN`,`BETWEEN`,`IS [NOT] NULL`),
  no subqueries/CTE/`HAVING`/`UNION`/`CASE`/window/string-date-math functions (only `DATEADD`/`GETUTCDATE` on
  literals in `WHERE`/`ON`); `GROUP BY` with `COUNT/SUM/AVG/MIN/MAX`; paging via `Prefer: odata.maxpagesize`
  (`TOP`/`OFFSET ... FETCH` not supported); aggregate queries capped at 50k evaluated rows. Evaluate value versus
  the existing FetchXML path (which already covers most of this) before committing.
