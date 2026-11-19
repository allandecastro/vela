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
    // Associate and Disassociate over N:N and 1:N relationships, and their relationship resolution.
    public sealed partial class InMemoryOrganizationService
    {
        // Non-throwing N:N resolution: a declared WithManyToMany relationship, else one recovered from registered
        // EntityMetadata. Used by Include reads to tell a N:N schema name from a 1:N/N:1 one.
        private bool TryResolveManyToMany(string schemaName, out RelationshipRegistry.ManyToMany relationship)
        {
            if (_relationships.TryResolve(schemaName, out relationship))
            {
                return true;
            }

            foreach (EntityMetadata meta in _metadata.Values)
            {
                ManyToManyRelationshipMetadata? m2m = meta.ManyToManyRelationships?
                    .FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));
                if (m2m != null)
                {
                    relationship = new RelationshipRegistry.ManyToMany(
                        m2m.IntersectEntityName,
                        m2m.Entity1LogicalName,
                        m2m.Entity1IntersectAttribute,
                        m2m.Entity2LogicalName,
                        m2m.Entity2IntersectAttribute);
                    return true;
                }
            }

            relationship = default;
            return false;
        }

        // Every N:N relationship the store knows about - declared via WithManyToMany and derived from registered
        // EntityMetadata - so a delete can clean up intersect rows on either side. Duplicates (a metadata N:N listed
        // on both entities) are harmless since intersect-row removal is idempotent.
        private IEnumerable<RelationshipRegistry.ManyToMany> AllManyToManyRelationships()
        {
            foreach (RelationshipRegistry.ManyToMany declared in _relationships.All)
            {
                yield return declared;
            }

            foreach (EntityMetadata meta in _metadata.Values)
            {
                if (meta.ManyToManyRelationships == null)
                {
                    continue;
                }

                foreach (ManyToManyRelationshipMetadata m2m in meta.ManyToManyRelationships)
                {
                    yield return new RelationshipRegistry.ManyToMany(
                        m2m.IntersectEntityName,
                        m2m.Entity1LogicalName,
                        m2m.Entity1IntersectAttribute,
                        m2m.Entity2LogicalName,
                        m2m.Entity2IntersectAttribute);
                }
            }
        }

        // Verifies the calling record and every related record exist before an Associate/Disassociate touches the
        // intersect, so relating to a missing record throws the platform not-found fault (ObjectDoesNotExist) exactly
        // as a real org does, rather than silently writing a dangling intersect row.
        private void DemandAssociationTargetsExist(string entityName, Guid entityId, EntityReferenceCollection relatedEntities)
        {
            if (!Table(entityName).ContainsKey(entityId))
            {
                throw RecordDoesNotExist(entityName, entityId);
            }

            foreach (EntityReference related in relatedEntities)
            {
                if (!Table(related.LogicalName).ContainsKey(related.Id))
                {
                    throw RecordDoesNotExist(related.LogicalName, related.Id);
                }
            }
        }

        /// <summary>
        /// Associates records. Over a many-to-many relationship (declared with <see cref="WithManyToMany(string, string, string, string, string, string)"/>
        /// or carried by registered metadata) it creates the intersect rows; <paramref name="entityName"/> and the
        /// related records must be the relationship's two sides. Associating a pair that is already associated faults,
        /// and nothing is written. Over a 1:N relationship declared with
        /// <see cref="WithRelationship(string, string, string, CascadeConfiguration, string)"/> it sets the child's lookup
        /// to the parent (an Update), from either side.
        /// </summary>
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            DemandStrictSecurityConfigured();

            // A 1:N "association" is not an intersect row - it sets the child's lookup to the parent (an Update). Only
            // an N:N relationship uses an intersect table. Try N:N first, then 1:N, matching the real Associate; an
            // undeclared relationship throws the standard guidance (before the target-existence check, as before).
            if (!TryResolveManyToMany(relationship.SchemaName, out RelationshipRegistry.ManyToMany rel))
            {
                if (_oneToMany.TryResolveBySchema(relationship.SchemaName, out OneToManyRegistry.OneToMany oneToMany))
                {
                    DemandRoleForReflexive(relationship, oneToMany.ReferencedEntity, oneToMany.ReferencingEntity);
                    DemandOneToManyAccess(entityName, entityId, oneToMany, relatedEntities);
                    DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
                    RunAtomically<object?>(() => { SetOneToManyLink(entityName, entityId, oneToMany, relationship.PrimaryEntityRole, relatedEntities, set: true); return null; });
                    return;
                }

                throw UnknownRelationship(relationship.SchemaName);
            }

            DemandRoleForReflexive(relationship, rel.FirstEntity, rel.SecondEntity);
            DemandManyToManyAccess(entityName, entityId, relatedEntities);
            DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
            DemandManyToManySides(relationship.SchemaName, rel, entityName, relatedEntities);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName, relationship.PrimaryEntityRole);
            Dictionary<Guid, Entity> intersect = Table(rel.IntersectEntity);
            EntityReference caller = new EntityReference(entityName, entityId);

            // Every pair is checked before any row is written, so a rejected pair (one already associated, or the
            // same record listed twice) leaves no partial association behind.
            HashSet<Guid> pending = new HashSet<Guid>();
            foreach (EntityReference related in relatedEntities)
            {
                bool exists = !pending.Add(related.Id) || intersect.Values.Any(row =>
                    EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), entityId) &&
                    EntityValues.AreEqual(EntityValues.GetValue(row, relatedAttribute), related.Id));
                if (exists)
                {
                    // Real Dataverse rejects re-associating an existing N:N pair (a plugin may catch that fault), so
                    // fault here rather than silently no-op'ing - the latter would let an access/dup test pass green.
                    throw AssociationAlreadyExists(entityName, entityId, related);
                }
            }

            foreach (EntityReference related in relatedEntities)
            {
                Entity row = new Entity(rel.IntersectEntity, _idSequence())
                {
                    [callerAttribute] = caller,
                    [relatedAttribute] = related,
                };
                intersect[row.Id] = row;
            }
        }

        // An N:N Associate links the relationship's two tables: the calling record must be one side and every related
        // record the other (the same table for a self-referential relationship). Anything else would write an intersect
        // row naming a table the relationship does not join. The fault code is not verified live.
        private static void DemandManyToManySides(string schemaName, RelationshipRegistry.ManyToMany relationship, string entityName, EntityReferenceCollection relatedEntities)
        {
            bool isFirst = string.Equals(entityName, relationship.FirstEntity, StringComparison.OrdinalIgnoreCase);
            bool isSecond = string.Equals(entityName, relationship.SecondEntity, StringComparison.OrdinalIgnoreCase);
            if (!isFirst && !isSecond)
            {
                throw PlatformFault.Create(PlatformFault.InvalidArgument,
                    $"Cannot associate a {entityName} record over the relationship '{schemaName}': it links {relationship.FirstEntity} and {relationship.SecondEntity}.");
            }

            string otherSide = isFirst ? relationship.SecondEntity : relationship.FirstEntity;
            foreach (EntityReference related in relatedEntities)
            {
                if (!string.Equals(related.LogicalName, otherSide, StringComparison.OrdinalIgnoreCase))
                {
                    throw PlatformFault.Create(PlatformFault.InvalidArgument,
                        $"Cannot associate {entityName} with a {related.LogicalName} record over the relationship '{schemaName}': it links {relationship.FirstEntity} and {relationship.SecondEntity}.");
                }
            }
        }

        // A relationship that does not exist faults with 0x80040216 (verified live). The store knows only the
        // relationships a test declares, so the message also says how to declare it.
        private static FaultException<OrganizationServiceFault> UnknownRelationship(string schemaName) =>
            PlatformFault.Create(PlatformFault.UnknownRelationship,
                $"The Entity Relationship with SchemaName '{schemaName}' was not found in the MetadataCache. Vela.Testing knows " +
                $"only the relationships a test declares: WithManyToMany(\"{schemaName}\", ...), WithRelationship(..., schemaName: " +
                $"\"{schemaName}\") (or WithParentalRelationship/WithReferentialRelationship), or an EntityMetadata carrying it " +
                $"registered with WithEntityMetadata(...).");

        // A self-referential (reflexive) relationship, N:N or 1:N, needs Relationship.PrimaryEntityRole to tell which
        // side the calling record is on; without it Associate/Disassociate fault (verified live).
        private static void DemandRoleForReflexive(Relationship relationship, string firstEntity, string secondEntity)
        {
            if (relationship.PrimaryEntityRole == null && string.Equals(firstEntity, secondEntity, StringComparison.OrdinalIgnoreCase))
            {
                throw PlatformFault.Create(PlatformFault.InvalidArgument,
                    $"Entity role must be specified for reflexive relationship '{relationship.SchemaName}.'");
            }
        }

        // Sets (Associate) or clears (Disassociate) a 1:N lookup: the child's ReferencingAttribute points at the
        // parent. Works from either side - entityName may be the parent (referenced) side, with relatedEntities the
        // children, or the child (referencing) side, with the single related record the parent. On a self-referential
        // relationship the role says which: Referenced makes the calling record the parent (verified live).
        private void SetOneToManyLink(string entityName, Guid entityId, OneToManyRegistry.OneToMany relationship, EntityRole? role, EntityReferenceCollection relatedEntities, bool set)
        {
            bool callerIsParent = role.HasValue
                ? role.Value == EntityRole.Referenced
                : string.Equals(entityName, relationship.ReferencedEntity, StringComparison.OrdinalIgnoreCase);
            foreach (EntityReference related in relatedEntities)
            {
                (string childEntity, Guid childId, EntityReference? parentRef) = callerIsParent
                    ? (related.LogicalName, related.Id, set ? new EntityReference(entityName, entityId) : null)
                    : (entityName, entityId, set ? related : null);

                if (set)
                {
                    // Verified live for Associate (Disassociate not probed): Read on the child row first.
                    DemandPlatformRead(childEntity, childId);
                }

                PlatformUpdate(new Entity(childEntity, childId) { [relationship.ReferencingAttribute] = parentRef });
            }
        }

        /// <summary>
        /// Disassociates records over a many-to-many relationship by deleting its matching intersect rows. The
        /// relationship must be declared with <see cref="WithManyToMany"/>.
        /// </summary>
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            DemandStrictSecurityConfigured();

            // 1:N disassociate clears the child's lookup (an Update); only N:N removes intersect rows. Resolve first so
            // an undeclared relationship throws the standard guidance before the target-existence check.
            if (!TryResolveManyToMany(relationship.SchemaName, out RelationshipRegistry.ManyToMany rel))
            {
                if (_oneToMany.TryResolveBySchema(relationship.SchemaName, out OneToManyRegistry.OneToMany oneToMany))
                {
                    DemandRoleForReflexive(relationship, oneToMany.ReferencedEntity, oneToMany.ReferencingEntity);
                    DemandOneToManyAccess(entityName, entityId, oneToMany, relatedEntities);
                    DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
                    RunAtomically<object?>(() => { SetOneToManyLink(entityName, entityId, oneToMany, relationship.PrimaryEntityRole, relatedEntities, set: false); return null; });
                    return;
                }

                throw UnknownRelationship(relationship.SchemaName);
            }

            DemandRoleForReflexive(relationship, rel.FirstEntity, rel.SecondEntity);
            DemandManyToManyAccess(entityName, entityId, relatedEntities);
            DemandAssociationTargetsExist(entityName, entityId, relatedEntities);
            (string callerAttribute, string relatedAttribute) = rel.AttributesFor(entityName, relationship.PrimaryEntityRole);
            Dictionary<Guid, Entity> intersect = Table(rel.IntersectEntity);
            HashSet<Guid> relatedIds = new HashSet<Guid>(relatedEntities.Select(r => r.Id));

            List<Guid> toRemove = intersect.Values
                .Where(row =>
                    EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), entityId) &&
                    relatedIds.Contains((Guid)(EntityValues.Normalize(EntityValues.GetValue(row, relatedAttribute)) ?? Guid.Empty)))
                .Select(row => row.Id)
                .ToList();

            foreach (Guid id in toRemove)
            {
                intersect.Remove(id);
            }
        }
    }
}
