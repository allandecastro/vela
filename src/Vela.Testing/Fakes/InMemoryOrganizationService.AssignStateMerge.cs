using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;

namespace Vela.Testing
{
    // The messages the platform runs as an Update of the record: Assign (ownerid), SetState (statecode/statuscode) and
    // Merge (the master's new values, the subordinate deactivated).
    public sealed partial class InMemoryOrganizationService
    {
        // Assign is not a message of an organization-owned table (verified live).
        internal void DemandAssignable(string entityLogicalName)
        {
            if (_systemFields.IsKnownOrganizationOwned(entityLogicalName))
            {
                throw PlatformFault.Create(PlatformFault.MessageNotSupportedForEntity,
                    $"The 'Assign' method does not support entities of type '{entityLogicalName}'.");
            }
        }

        internal Entity SetStateUpdate(EntityReference record, OptionSetValue? state, OptionSetValue? status)
        {
            int stateValue = state?.Value ?? 0;
            int statusValue = status != null && status.Value >= 0 ? status.Value : DefaultStatus(record.LogicalName, stateValue);
            return new Entity(record.LogicalName, ResolveReference(record))
            {
                [SystemFieldApplier.StateCode] = new OptionSetValue(stateValue),
                [SystemFieldApplier.StatusCode] = new OptionSetValue(statusValue),
            };
        }

        // The only tables Dataverse supports Merge on.
        private static readonly HashSet<string> MergeableEntities =
            new HashSet<string>(new[] { "account", "contact", "lead", "incident" }, StringComparer.OrdinalIgnoreCase);

        // Merge (account/contact/lead/incident): applies UpdateContent to the target (ignored for incident), reparents
        // the subordinate's 1:N children (Merge = Cascade) and moves its N:N associations to the target, then
        // deactivates the subordinate. The subordinate's own columns are NOT copied to the target (verified live, with
        // UpdateContent null or empty), and an inactive subordinate faults. All-or-nothing. The pipeline passes writeMaster to run the master's Update
        // through its Update steps; without it the store writes the master itself.
        internal void Merge(MergeRequest request, Action<Entity>? writeMaster)
        {
            EntityReference target = request.Target ?? throw new ArgumentNullException(nameof(request), "Merge Target is required.");
            string entity = target.LogicalName;
            Guid subordinateId = request.SubordinateId;

            if (!MergeableEntities.Contains(entity))
            {
                throw new InvalidPluginExecutionException(
                    $"Merge is not supported for '{entity}'. Dataverse supports Merge only for account, contact, lead and incident.");
            }

            if (!Table(entity).TryGetValue(target.Id, out Entity? targetRecord))
            {
                throw RecordDoesNotExist(entity, target.Id);
            }

            if (!Table(entity).TryGetValue(subordinateId, out Entity? subordinate))
            {
                throw RecordDoesNotExist(entity, subordinateId);
            }

            // Verified live: merging a subordinate that is already inactive (merged before) faults.
            if (subordinate!.GetAttributeValue<OptionSetValue>(SystemFieldApplier.StateCode)?.Value is int subordinateState && subordinateState != 0)
            {
                throw PlatformFault.Create(PlatformFault.MergeSubordinateInactive, $"sub-entity:{subordinateId} of type '{entity}' is deactive");
            }

            // Verified live for a caller without Read on the table: Merge checks Read before writing anything. Which
            // of the two rows it checks was not isolated, so both are.
            DemandPlatformRead(entity, target.Id);
            DemandPlatformRead(entity, subordinateId);

            bool isIncident = string.Equals(entity, "incident", StringComparison.OrdinalIgnoreCase);

            RunAtomically<object?>(() =>
            {
                // 1. UpdateContent sets values on the target (ignored when merging incidents). Nothing else is written
                //    to the target: the subordinate's columns are not copied over (verified live).
                Entity merged = new Entity(entity, target.Id);
                if (request.UpdateContent != null && !isIncident)
                {
                    foreach (KeyValuePair<string, object> attribute in request.UpdateContent.Attributes)
                    {
                        if (!IsMergeSkippedColumn(entity, attribute.Key))
                        {
                            merged[attribute.Key] = EntityValues.CloneValue(attribute.Value);
                        }
                    }
                }

                if (merged.Attributes.Count > 0)
                {
                    if (writeMaster != null)
                    {
                        writeMaster(merged);
                    }
                    else
                    {
                        Update(merged);
                    }
                }

                // 2. Reparent the subordinate's 1:N children to the target, except over a relationship whose Merge
                //    behaviour is NoCascade (unset counts as Cascade, the platform's referential default), and move
                //    its N:N associations (verified live).
                foreach (OneToManyRegistry.OneToMany relationship in _oneToMany.ChildRelationshipsOf(entity)
                             .Where(relationship => relationship.Cascade.Merge != CascadeType.NoCascade).ToList())
                {
                    foreach (Entity child in Children(relationship, subordinateId).ToList())
                    {
                        PlatformUpdate(new Entity(child.LogicalName, child.Id)
                        {
                            [relationship.ReferencingAttribute] = new EntityReference(entity, target.Id),
                        });
                    }
                }

                MoveIntersectRows(entity, subordinateId, target.Id);

                // 3. Deactivate the subordinate (Inactive = statecode 1 / statuscode 2) and point its masterid at the
                //    target (verified live: the subordinate's post-image carries both).
                PlatformUpdate(new Entity(entity, subordinateId)
                {
                    ["statecode"] = new OptionSetValue(1),
                    ["statuscode"] = new OptionSetValue(2),
                    ["masterid"] = new EntityReference(entity, target.Id),
                });
                return null;
            });
        }

        private static bool IsMergeSkippedColumn(string entity, string column) =>
            string.Equals(column, entity + "id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "statecode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "statuscode", StringComparison.OrdinalIgnoreCase);
    }
}
