---
_description: "Opt-in security in the in-memory service: roles, privileges, business units, teams, sharing"
---

# Security model in tests

By default the in-memory service of [`Vela.Testing`](testing.md) grants **full access**: most plugin tests do not
need security. Opt in with `WithSecurity(...)` to test plugins that depend on access rights (privilege-denied paths,
`RetrievePrincipalAccess`, sharing). This page covers how to declare the model and what the harness enforces.

You declare business units, named roles, users and teams. The service then enforces the table privileges (Create,
Read, Write, Delete, Assign, Append, AppendTo, Share) at **User, BusinessUnit, ParentChild or Organization** depth,
over the user and every team they belong to. A denied write throws `FaultException<OrganizationServiceFault>` with
the `PrincipalPrivilegeDenied` code (`0x80040220`). Reads follow the rules in [Reads](#reads).

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#security)]

The same configuration works on `PluginPipeline` (see [Pipeline tests](#pipeline-tests)). For how Dataverse itself
evaluates access, see
[How access to a record is determined](https://learn.microsoft.com/power-platform/admin/how-record-access-determined).

## Declare the model

`WithSecurity(s => ...)` takes a `SecurityConfiguration` (namespace `Vela.Testing.Security`):

| Method | Declares |
| --- | --- |
| `BusinessUnit(id, parent)` | A business unit, under its parent (none for the root). |
| `Role(name, r => r.Grant(table, AccessLevel.X, Privilege.Y, ...))` | A named role and its grants. |
| `User(id, businessUnit, roles...)` | A user in a business unit, with its roles. |
| `Team(id, businessUnit, roles...)` | A team with its roles. `Team(id, businessUnit, TeamPrivilegeInheritance.TeamOnly, roles...)` gives the members the team's User-level grants only for records the team owns. |
| `Member(userId, teamId)` | A team membership: the member uses the team's roles. |

`Role(...)` also takes an id, a template id and the grant tuples of a generated `Roles` class (see
[Role membership](#role-membership)).

## Arrange with Seed, act with Create

`Seed(...)` writes records straight into the store, without privilege checks: it is your starting state, the data
that already exists. `Create(...)` demands the **Create** privilege, so use it only when the create itself is under
test. A test such as "Bob owns this record, can Alice read it?" seeds Bob's record and asserts on Alice's `Retrieve`
or `Update`. Seeding never fails on a missing privilege, so the assertion is about the access you mean to test.

`Seed` does not set system columns either. Set `ownerid` on a seeded record, and `owningbusinessunit` when the test
relies on BusinessUnit or ParentChild depth: the harness reads the record's business unit from that column.

## Reads

The harness checks the Read privilege first, then whether the record is in reach:

- **No Read privilege on a table**, at any depth: the whole read throws `PrincipalPrivilegeDenied` (`0x80040220`,
  "is missing prvRead... privilege"). This applies to the root table of a `Retrieve` or `RetrieveMultiple`
  (QueryExpression, FetchXML, QueryByAttribute, typed queries) and to the table of every link-entity, inner or outer,
  with or without columns or filters, at any nesting level.
- **Privilege held, record out of reach** (depth, owner, no share): `RetrieveMultiple` leaves the row out, and a
  `Retrieve` by id throws `0x80048306` ("does not have ReadAccess right(s) for record").
- **Link to a record out of reach**: an inner link drops the root row; an outer link keeps it with no linked columns,
  so a `null` condition on the link alias matches it.
- A lookup on a row the user can read keeps its value, even when the target record is out of reach.

## Ownership and assignment

Owning a record (directly or through a team) satisfies the User depth. Changing `ownerid` (with `AssignRequest` or a
plain `Update`) needs the **Assign** privilege, and the owning business unit follows the new owner. A new owner the
security model knows must hold Read on the table (User level is enough), whoever sets it, a PreOperation plugin
included; otherwise the Create, Update or Assign fails with `0x80040299` (`0x80042f0a` when an owner team has no role,
on Update or Assign). See [Record ownership](https://learn.microsoft.com/power-platform/admin/update-record-owner#record-ownership).

## Sharing

`GrantAccess`, `ModifyAccess` and `RevokeAccess` share a record with a user or team. Shared access is added to role
access (a share only adds access) and cannot exceed the sharer's own rights. A shared right counts only when the
sharee holds that privilege on the table at some depth (User is enough), so a user with no role on the table reaches
nothing through a share.

- `RetrievePrincipalAccess` returns the effective rights.
- `RetrieveSharedPrincipalsAndAccess` lists a record's explicit shares.
- `WithSharedRecord(record, principal, AccessRights.ReadAccess)` shares a record during arrange. It does not cascade.

See [Sharing and assigning](https://learn.microsoft.com/power-apps/developer/data-platform/security-sharing-assigning).

## Relationship cascades

Declare 1:N relationships with `WithParentalRelationship(...)`, `WithReferentialRelationship(..., restrictDelete: true)`
or, for any other cascade configuration, `WithRelationship(..., cascade)`. Deleting a parent then cascades per the
relationship: **Cascade** deletes the children, **RemoveLink** clears their lookup, and **Restrict** blocks the delete
while children exist. A Restrict met anywhere in the cascade, on a grandchild for example, fails the whole delete and
leaves every record as it was. Assign cascades the same way. Each method takes an optional `schemaName`, the
relationship's real schema name (see [Relationships](testing.md#relationships)).

A table can be the child of only one parental relationship (one where Assign, Reparent, Share or Unshare cascades, or
Delete is Cascade). Declaring a second one throws, unless it is over the same lookup column (a polymorphic lookup
such as a customer lookup to account and contact).

## Inherited access

A child of a parental relationship gets **inherited grants**, kept apart from explicit shares:
`RetrieveSharedPrincipalsAndAccess` does not list them, and `RetrievePrincipalAccess` counts them.

- **Reparent cascade.** When the child is created under a parent, or its lookup is moved to one, it gets a grant
  for the parent's owner (user or team, with the owner's effective rights on the parent) and for every principal the
  parent is already shared with, explicitly or by inheritance (with that grant's rights). Moving the child replaces
  the grants from its previous parent.
- **Seeded children.** A seeded child is treated like a created one, whatever the setup order: its grants are derived
  again when `WithSecurity`, a relationship or a `WithSharedRecord` comes after the `Seed`, so a share set up on the
  parent is inherited by a child seeded before it. A child created with `Create` keeps the grants it got when it was
  created.
- **Share cascade.** `GrantAccess` or `ModifyAccess` on the parent adds or replaces an inherited grant on its children
  in scope (recursively). `RevokeAccess` with the **Unshare** cascade removes it and leaves the children's own shares.
- Like a share, an inherited grant counts only for a privilege the principal holds on the child table. Role access
  to the parent confers nothing on the children.
- Only the record whose parent changes is derived again; its existing descendants keep their grants. A change of
  the parent's owner (Assign) does not derive the children's grants again.

See [Cascading behavior](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-entity-relationships)
and [inherited access](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/user-permissions/cleanup-inherited-access).

## Access teams

`WithAccessTeamTemplate(templateId, rights)` registers a template's rights. A plugin then grants a user those rights
on a record with `AddUserToRecordTeamRequest`, and removes them with `RemoveUserFromRecordTeamRequest`. They are
added to role and shared access. Reading the record still needs the Read privilege on the table (see [Reads](#reads)).

## Hierarchy security

- **Manager hierarchy.** `ManagerHierarchy(depth)` with `Manager(userId, managerId)` lets a manager reach a report's
  records up the reporting chain: read and write on a direct report's records, read-only deeper (within the depth).
  The manager must already hold at least User-level access for that privilege and be in the report's business unit
  or its parent.
- **Position hierarchy.** `PositionHierarchy(depth)` with `Position(userId, positionId)` and
  `PositionParent(positionId, parentId)` applies the same read/write and read-only rule, keyed on positions and
  **across business units**.

A report's record is reached whether the report owns it, the report's team owns it, or it is shared with the report
(explicitly or by an inherited grant).

## Field-level security

`SecureColumn(table, column)` marks a column secured, and `FieldPermission(principal, table, column, read:, update:)`
grants it. Without permission, a secured column is hidden on read and rejected on write, whatever the record access.

## Role membership

With security configured, the platform's membership tables (`role`, `systemuserroles`, `teamroles`,
`teammembership`) can be queried, so a plugin resolves a user's roles as it does against Dataverse.

- Through the user's service, `role` follows the [Reads](#reads) rules like any table: it needs the Read privilege
  on `role` (a query that links an intersect table to `role` included), and each copy belongs to its
  `businessunitid`. A copy without one (when there is no single root business unit) is reached at Organization
  depth only. The intersect tables have no read check. `ctx.SystemService` reads all of them.
- The harness stores one `role` row per business unit. The root business unit's copy keeps the declared id
  (`roleid == parentrootroleid`); every other copy gets its own stable id and points at the root through
  `parentrootroleid`. A role declared with a template id carries it as `roletemplateid` on every copy.
- `systemuserroles.roleid` and `teamroles.roleid` hold the copy of the user's or team's business unit, so the
  declared (root) id matches them only in the root business unit. Match `role.parentrootroleid` to the root id
  instead.
- The root business unit is the single top of the declared `BusinessUnit(...)` hierarchy or, with none declared, the
  single business unit the users and teams are in. When there is no single top, every principal holds the root id.
- Privilege checks read the declared roles, not these rows.

To test against the real roles of your environment, generate a `Roles` class with
[`vela models --roles`](models.md) and declare each role with `Role(Roles.XName, Roles.XId, Roles.X)` (for a system
role, `Role(Roles.XName, Roles.XId, Roles.XTemplateId, Roles.X)`). The `--roles` section of [Models](models.md) shows
the membership query. For the platform's `role` columns, see the
[Security Role table reference](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/role).

## Strict security

A test that never calls `WithSecurity(...)` gets full access. `WithStrictSecurity()` turns that into a failure: a
security-relevant operation (Create, Retrieve, RetrieveMultiple, Update, Delete, ...) run without a configured
security model throws, so an access test that forgot `WithSecurity(...)` cannot pass.

## Pipeline tests

`PluginPipeline` has the same `WithCurrentUser`, `WithSecurity`, `WithStrictSecurity`, `WithSharedRecord`,
`WithAccessTeamTemplate` and relationship methods. Under `WithSecurity`, `ctx.SystemService` acts as SYSTEM and
bypasses the model, while `ctx.Service` is checked as the step's user (the impersonated user when the step has one).
