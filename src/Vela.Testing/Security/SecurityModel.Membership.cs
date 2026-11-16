using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    // The security-membership rows (role copies per business unit, systemuserroles, teamroles, teammembership) seeded
    // into the store so plugins can query role membership like the platform.
    internal sealed partial class SecurityModel
    {
        /// <summary>
        /// The security-membership intersect rows to seed into the store so plugins can query role membership like
        /// the platform: <c>role</c> (one copy per business unit), <c>systemuserroles</c> (a user's direct roles),
        /// <c>teamroles</c> (a team's roles) and <c>teammembership</c> (a user's teams).
        /// </summary>
        /// <remarks>
        /// Dataverse keeps one copy of a security role per business unit. The root business unit's copy carries the
        /// role's root id (<c>roleid == parentrootroleid</c>); every other copy has its own id and points at the root
        /// through <c>parentrootroleid</c>. <c>systemuserroles.roleid</c> / <c>teamroles.roleid</c> hold the copy of
        /// the principal's business unit. The declared role id (a generated <c>Roles.XId</c>) is the root id, so the
        /// root copy keeps it and the other copies get an id derived from (root id, business unit), stable across
        /// runs. A system role declared with a template id carries it as <c>roletemplateid</c> on every copy; a custom
        /// role has none. When no single root business unit can be identified (no declared hierarchy and principals spread over
        /// several unrelated business units), every principal is treated as being in the root and gets the root id.
        /// Privilege evaluation is unaffected: it reads the declared roles, not these rows.
        /// </remarks>
        public IEnumerable<Entity> MaterializeMembershipTables()
        {
            Guid? root = RootBusinessUnit();
            List<Guid> copies = root.HasValue ? NonRootBusinessUnits(root.Value) : new List<Guid>();

            foreach (KeyValuePair<string, Guid> role in _roleIds)
            {
                EntityReference? template = _roleTemplateIds.TryGetValue(role.Key, out Guid templateId)
                    ? new EntityReference("roletemplate", templateId)
                    : null;
                Entity rootCopy = new Entity("role", role.Value)
                {
                    ["roleid"] = role.Value,
                    ["name"] = role.Key,
                    ["parentrootroleid"] = new EntityReference("role", role.Value),
                };
                if (root.HasValue)
                {
                    rootCopy["businessunitid"] = new EntityReference("businessunit", root.Value);
                }

                if (template != null)
                {
                    rootCopy["roletemplateid"] = template;
                }

                yield return rootCopy;

                foreach (Guid businessUnit in copies)
                {
                    Guid copyId = RoleCopyId(role.Value, businessUnit);
                    Entity copy = new Entity("role", copyId)
                    {
                        ["roleid"] = copyId,
                        ["name"] = role.Key,
                        ["parentrootroleid"] = new EntityReference("role", role.Value),
                        ["businessunitid"] = new EntityReference("businessunit", businessUnit),
                    };
                    if (template != null)
                    {
                        copy["roletemplateid"] = new EntityReference(template.LogicalName, template.Id);
                    }

                    yield return copy;
                }
            }

            foreach (Principal user in _users.Values)
            {
                foreach (Guid roleId in RoleIds(user.Roles))
                {
                    yield return Intersect("systemuserroles", "systemuserid", user.Id, "roleid", CopyFor(roleId, user.BusinessUnit, root));
                }
            }

            foreach (Principal team in _teams.Values)
            {
                foreach (Guid roleId in RoleIds(team.Roles))
                {
                    yield return Intersect("teamroles", "teamid", team.Id, "roleid", CopyFor(roleId, team.BusinessUnit, root));
                }
            }

            foreach (KeyValuePair<Guid, HashSet<Guid>> membership in _memberships)
            {
                foreach (Guid teamId in membership.Value)
                {
                    yield return Intersect("teammembership", "systemuserid", membership.Key, "teamid", teamId);
                }
            }
        }

        // The id of a role's copy in a business unit: the root id in the root BU (or when the root is unknown, or the
        // principal has no BU), otherwise a deterministic id derived from (root id, BU).
        private static Guid CopyFor(Guid rootRoleId, Guid businessUnit, Guid? root) =>
            !root.HasValue || businessUnit == Guid.Empty || businessUnit == root.Value
                ? rootRoleId
                : RoleCopyId(rootRoleId, businessUnit);

        private static Guid RoleCopyId(Guid rootRoleId, Guid businessUnit) =>
            DeterministicGuid.From("role", rootRoleId, businessUnit);

        private IEnumerable<Guid> RoleIds(IReadOnlyList<string> roleNames)
        {
            foreach (string name in roleNames)
            {
                if (_roleIds.TryGetValue(name, out Guid id))
                {
                    yield return id;
                }
            }
        }

        private static Entity Intersect(string table, string firstColumn, Guid firstId, string secondColumn, Guid secondId) =>
            new Entity(table, DeterministicGuid.From(table, firstId, secondId))
            {
                [firstColumn] = firstId,
                [secondColumn] = secondId,
            };
    }
}
