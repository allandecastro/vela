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

- **Sharing (PoA).** `GrantAccess`/`ModifyAccess`/`RevokeAccess` share a specific record with a user or team -
  unioned with role access (a share only ever *adds* access, and can't exceed the sharer's own rights).
  `RetrievePrincipalAccess` returns the effective rights, `RetrieveSharedPrincipalsAndAccess` lists a record's
  shares, and `WithSharedRecord(record, principal, AccessRights.ReadAccess)` pre-shares in arrange.
- **Ownership & assignment.** Owning a record (directly or via a team) satisfies the User depth; changing `ownerid`
  (through `AssignRequest` or a plain Update) needs the **Assign** privilege, and the owning business unit follows
  the new owner.
- **Relationship cascades.** Declare 1:N relationships with `WithParentalRelationship(...)` /
  `WithReferentialRelationship(..., restrictDelete: true)`. Deleting a parent then cascades per the relationship -
  **Cascade** deletes the children, **RemoveLink** clears their lookup, **Restrict** blocks the delete while
  children exist. Assign and Share cascade the same way, and in a parental relationship a child **inherits** the
  caller's access to its parent (re-derived automatically when the child is re-parented).
- **Access teams.** `WithAccessTeamTemplate(templateId, rights)` registers a template's rights; a plugin then
  grants a user those rights on a record with `AddUserToRecordTeamRequest` (and removes them with
  `RemoveUserFromRecordTeamRequest`) - unioned with role and shared access.
- **Hierarchy security.** `ManagerHierarchy(depth)` + `Manager(userId, managerId)` let a manager reach a report's
  records up the reporting chain: read/write on a direct report, read-only deeper (within depth), provided they
  already hold at least User-level access for that privilege and are in the report's business unit or its parent.
  `PositionHierarchy(depth)` + `Position(userId, positionId)` / `PositionParent(positionId, parentId)` model the
  position hierarchy instead - the same read/write-vs-read-only rule, but keyed on positions and **across business
  units** (no BU constraint). A "report" is reached whether it owns the record, its team owns it, or it's shared to
  the report.
- **Field-level security.** `SecureColumn(entity, column)` marks a column secured; `FieldPermission(principal,
  entity, column, read:, update:)` grants it. A secured column is hidden on read and rejected on write without
  permission - independent of record access.
- **Role membership.** With security configured, the platform's membership tables (`role`, `systemuserroles`,
  `teamroles`, `teammembership`) are queryable, so a plugin can resolve a user's roles as it does against
  Dataverse. Generate a `Roles` class with the **real** role privileges + ids from your environment with
  [`vela models --roles`](models.md) (dependency-light, generated into the shared models project), then
  `Role(Roles.XName, Roles.XId, Roles.X)`.
- **Off by default** - any test that never calls `WithSecurity(...)` behaves exactly as before (full access).
