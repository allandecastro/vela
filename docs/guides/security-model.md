# Security model in tests

By default the in-memory service grants **full access** - most plugin tests don't need security. Opt in with
`WithSecurity(...)` to exercise plugins that branch on access rights (privilege-denied paths,
`RetrievePrincipalAccess`, sharing). You declare business units, named roles, users and teams; the service then
enforces the table privileges (Create/Read/Write/Delete/Assign/Append/AppendTo/Share) at
**User / BusinessUnit / ParentChild / Organization** depth, over the user and every team they belong to. A denied
write throws `FaultException<OrganizationServiceFault>` with the `PrincipalPrivilegeDenied` code; `RetrieveMultiple`
silently filters to the records the user may read.

```csharp
using Vela.Testing.Security;

var crm = new InMemoryOrganizationService()
    .WithCurrentUser(aliceId)
    .WithSecurity(s => s
        .BusinessUnit(salesBu)
        .Role("salesperson", r => r
            .Grant("account", AccessLevel.BusinessUnit, Privilege.Create, Privilege.Read, Privilege.Write)
            .Grant("contact", AccessLevel.Organization, Privilege.Read))
        .User(aliceId, salesBu, "salesperson")
        .Team(salesTeam, salesBu, "salesperson")   // roles work on teams too
        .Member(aliceId, salesTeam))                // ...and members inherit the team's roles
    .Seed(/* records */);

// Alice reads/writes accounts in her business unit; records she cannot see are filtered out of
// RetrieveMultiple, and a write she lacks the privilege for throws PrincipalPrivilegeDenied.
```

- **Arrange vs act.** `Seed(...)` writes records straight into the store, bypassing privilege checks (and the
  system-column stamping) - it is your *starting state*, the data that already exists. `Create(...)` instead
  demands the **Create** privilege, so reach for it only when the create itself is under test. A "Bob owns this
  record, can Alice read it?" test therefore *seeds* Bob's record and asserts on Alice's `Retrieve`/`Update`;
  seeding it never fails on a missing privilege, which keeps the assertion about the access you mean to test.
- **Sharing (PoA).** `GrantAccess`/`ModifyAccess`/`RevokeAccess` share a specific record with a user or team -
  unioned with role access (a share only ever *adds* access, and can't exceed the sharer's own rights). A shared
  right only counts when the sharee holds that privilege on the table at some access level (User is enough), so a
  user with no role on the table reaches nothing through a share. `RetrievePrincipalAccess` returns the effective
  rights, `RetrieveSharedPrincipalsAndAccess` lists a record's explicit shares, and
  `WithSharedRecord(record, principal, AccessRights.ReadAccess)` pre-shares one record in arrange (it does not
  cascade). See [Sharing and assigning](https://learn.microsoft.com/power-apps/developer/data-platform/security-sharing-assigning).
- **Ownership & assignment.** Owning a record (directly or via a team) satisfies the User depth; changing `ownerid`
  (through `AssignRequest` or a plain Update) needs the **Assign** privilege, and the owning business unit follows
  the new owner.
- **Relationship cascades.** Declare 1:N relationships with `WithParentalRelationship(...)` /
  `WithReferentialRelationship(..., restrictDelete: true)`. Deleting a parent then cascades per the relationship -
  **Cascade** deletes the children, **RemoveLink** clears their lookup, **Restrict** blocks the delete while
  children exist. Assign cascades the same way. A table can be the child of only one parental relationship (one
  where Assign/Reparent/Share/Unshare cascades or Delete is Cascade): declaring a second one throws, unless it is
  over the same lookup column (a polymorphic lookup such as a customer lookup to account and contact).
- **Inherited access.** A child of a parental relationship gets **inherited grants**, kept apart from explicit
  shares (`RetrieveSharedPrincipalsAndAccess` does not list them, `RetrievePrincipalAccess` counts them):
  - **Reparent** cascade: when the child is created under a parent, or its lookup is moved to one, it gets a grant
    for the parent's owner (user or team; the owner's effective rights on the parent) and for every principal the
    parent is already shared with, explicitly or by inheritance (that grant's rights). Moving the child replaces
    the grants from its previous parent. A seeded child (`Seed`) is treated like a created one, so configure
    `WithSecurity` first.
  - **Share** cascade: `GrantAccess`/`ModifyAccess` on the parent adds/replaces an inherited grant on its children
    in scope (recursively); `RevokeAccess` with the **Unshare** cascade removes it, leaving the children's own shares.
  - Like a share, an inherited grant only counts for a privilege the principal holds on the child table. Role
    access to the parent confers nothing on the children.
  - Only the record whose parent changes is re-derived; its existing descendants keep their grants. A change of the
    parent's owner (Assign) does not re-derive the children's grants.

  See [Cascading behavior](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-entity-relationships)
  and [inherited access](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/user-permissions/cleanup-inherited-access).
- **Access teams.** `WithAccessTeamTemplate(templateId, rights)` registers a template's rights; a plugin then
  grants a user those rights on a record with `AddUserToRecordTeamRequest` (and removes them with
  `RemoveUserFromRecordTeamRequest`) - unioned with role and shared access.
- **Hierarchy security.** `ManagerHierarchy(depth)` + `Manager(userId, managerId)` let a manager reach a report's
  records up the reporting chain: read/write on a direct report, read-only deeper (within depth), provided they
  already hold at least User-level access for that privilege and are in the report's business unit or its parent.
  `PositionHierarchy(depth)` + `Position(userId, positionId)` / `PositionParent(positionId, parentId)` model the
  position hierarchy instead - the same read/write-vs-read-only rule, but keyed on positions and **across business
  units** (no BU constraint). A "report" is reached whether it owns the record, its team owns it, or it's shared to
  the report (explicitly or by an inherited grant).
- **Field-level security.** `SecureColumn(entity, column)` marks a column secured; `FieldPermission(principal,
  entity, column, read:, update:)` grants it. A secured column is hidden on read and rejected on write without
  permission - independent of record access.
- **Role membership.** With security configured, the platform's membership tables (`role`, `systemuserroles`,
  `teamroles`, `teammembership`) are queryable, so a plugin can resolve a user's roles as it does against
  Dataverse. Generate a `Roles` class with the **real** role privileges + ids from your environment with
  [`vela models --roles`](models.md) (dependency-light, generated into the shared models project), then
  `Role(Roles.XName, Roles.XId, Roles.X)`.
- **Off by default** - a test that never calls `WithSecurity(...)` gets full access. `WithStrictSecurity()` turns
  that into a failure: a security-relevant operation (Create, Retrieve, RetrieveMultiple, Update, Delete, ...) run
  without a configured security model throws, so an access test that forgot `WithSecurity(...)` cannot pass green.
- **Pipeline tests** - `PluginPipeline` has the same `WithCurrentUser`, `WithSecurity`, `WithStrictSecurity`,
  `WithSharedRecord`, `WithAccessTeamTemplate` and relationship methods. Under `WithSecurity`, `ctx.SystemService`
  is SYSTEM and bypasses the model, while `ctx.Service` is checked as the step's user (the impersonated user when
  the step has one).
