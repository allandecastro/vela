using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    // Hierarchy security (manager and position models): what a higher user reaches of its reports' records.
    internal sealed partial class SecurityModel
    {
        // The record-level rights the hierarchy can extend from a report onto a higher user. Delete/Assign/Share are
        // never conferred by hierarchy, so an owner is treated as holding only these on their own records for the walk.
        private const AccessRights HierarchyRights =
            AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess | AccessRights.AppendToAccess;

        // Hierarchy security: a higher user (manager or position) gets Read on any report's record within the configured
        // depth, and Write/Append/AppendTo on a DIRECT report (distance 1) only — never Delete/Assign/Share. The gate is
        // at least User-level READ on the table (per MS: "a manager must have at least the user level Read privilege to
        // see the reports' data") — once read is held the model confers the direct-report write set regardless of the
        // higher user's own write privilege. A "report" is any user with access to the record: its owning user, a member
        // of its owning team, or a user the record is shared to (PoA), directly or via a team.
        // The manager model requires the higher user to be in the report's BU or its parent; the position model does not
        // (it spans business units).
        private bool HasHierarchyAccess(Guid userId, string table, Privilege privilege, Entity record)
        {
            if (_hierarchyDepth <= 0)
            {
                return false;
            }

            bool wantsWrite = privilege == Privilege.Write || privilege == Privilege.Append || privilege == Privilege.AppendTo;
            if (privilege != Privilege.Read && !wantsWrite)
            {
                return false; // Delete/Assign/Share/Create are never conferred by the hierarchy
            }

            // Precondition (MS hierarchy security): the higher user must hold at least User-level Read on the table —
            // that is the single gate; the direct-report write set is then conferred by the model itself.
            if (GrantedLevels(userId, table, Privilege.Read).Count == 0)
            {
                return false;
            }

            AccessRights required = ToAccessRight(privilege);
            foreach ((Guid report, AccessRights held) in ReportsWithAccess(table, record))
            {
                if (report == userId || (held & required) != required)
                {
                    continue; // the report doesn't itself hold the requested right on this record
                }

                int distance = HierarchyDistance(report, userId);
                if (distance == 0)
                {
                    continue; // userId is not a higher manager/position of this report within depth
                }

                if (privilege == Privilege.Read || distance == 1)
                {
                    return true; // read at any depth; write-type only for a direct (distance-1) report
                }
            }

            return false;
        }

        // Every user that has direct access to the record, tagged with the rights they hold: the owning user (all
        // hierarchy rights), each member of the owning team (all), and each user a PoA share or inherited grant reaches —
        // directly or via a team it belongs to — with the granted mask. This is the set of "reports" whose access the hierarchy extends up.
        private IEnumerable<(Guid User, AccessRights Held)> ReportsWithAccess(string table, Entity record)
        {
            Dictionary<Guid, AccessRights> access = new Dictionary<Guid, AccessRights>();
            void Add(Guid user, AccessRights rights)
            {
                access[user] = (access.TryGetValue(user, out AccessRights existing) ? existing : AccessRights.None) | rights;
            }

            if (record.GetAttributeValue<EntityReference>(SystemFieldApplier.OwnerId) is EntityReference owner)
            {
                if (PrincipalKind.IsUser(owner))
                {
                    Add(owner.Id, HierarchyRights);
                }
                else if (PrincipalKind.IsTeam(owner))
                {
                    foreach (Guid member in MembersOf(owner.Id))
                    {
                        Add(member, HierarchyRights);
                    }
                }
            }

            EntityReference target = new EntityReference(table, record.Id);
            foreach (PrincipalAccess share in _sharing.SharesOf(target).Concat(_sharing.InheritedOf(target)))
            {
                if (PrincipalKind.IsUser(share.Principal))
                {
                    Add(share.Principal.Id, share.AccessMask);
                }
                else if (PrincipalKind.IsTeam(share.Principal))
                {
                    foreach (Guid member in MembersOf(share.Principal.Id))
                    {
                        Add(member, share.AccessMask);
                    }
                }
            }

            return access.Select(entry => (entry.Key, entry.Value));
        }

        // How many hierarchy levels separate a report from a higher user (1 = direct), via the manager chain or the
        // position tree, or 0 if the higher user is neither above the report within depth. The nearer of the two wins.
        private int HierarchyDistance(Guid report, Guid higher)
        {
            int manager = ManagerDistance(report, higher);
            int position = PositionDistance(report, higher);
            if (manager == 0)
            {
                return position;
            }

            return position == 0 ? manager : Math.Min(manager, position);
        }

        // Distance up the manager chain (report -> ... -> higher), bounded by depth, subject to the manager-model BU
        // rule: the higher user must be in the report's business unit or its parent. 0 if not reachable.
        private int ManagerDistance(Guid report, Guid higher)
        {
            if (_managers.Count == 0)
            {
                return 0;
            }

            Guid higherBu = BusinessUnitOf(higher);
            Guid reportBu = BusinessUnitOf(report);
            bool businessUnitOk = higherBu != Guid.Empty &&
                (higherBu == reportBu || (_businessUnitParents.TryGetValue(reportBu, out Guid parent) && higherBu == parent));
            if (!businessUnitOk)
            {
                return 0;
            }

            Guid current = report;
            HashSet<Guid> seen = new HashSet<Guid> { current };
            for (int level = 1; level <= _hierarchyDepth; level++)
            {
                if (!_managers.TryGetValue(current, out Guid manager) || !seen.Add(manager))
                {
                    break;
                }

                if (manager == higher)
                {
                    return level;
                }

                current = manager;
            }

            return 0;
        }

        // Distance up the position tree: the report's position must be a strict descendant of the higher user's
        // position within depth. Position hierarchy spans business units, so no BU rule applies. 0 if not reachable.
        private int PositionDistance(Guid report, Guid higher)
        {
            if (!_positions.TryGetValue(report, out Guid reportPosition) ||
                !_positions.TryGetValue(higher, out Guid higherPosition) ||
                reportPosition == higherPosition)
            {
                return 0; // no positions, or peers sharing a position (not "higher")
            }

            Guid current = reportPosition;
            HashSet<Guid> seen = new HashSet<Guid> { current };
            for (int level = 1; level <= _hierarchyDepth; level++)
            {
                if (!_positionParents.TryGetValue(current, out Guid parent) || !seen.Add(parent))
                {
                    break;
                }

                if (parent == higherPosition)
                {
                    return level;
                }

                current = parent;
            }

            return 0;
        }
    }
}
