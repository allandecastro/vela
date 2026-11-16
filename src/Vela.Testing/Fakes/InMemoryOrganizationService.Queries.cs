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
    // Reads beyond a single record: RetrieveMultiple (executed by InMemoryQueryEngine) and the related records a
    // Retrieve returns through RelatedEntitiesQuery.
    public sealed partial class InMemoryOrganizationService
    {
        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            // Strict security: a read run with no configured security model fails loudly instead of returning
            // everything, so an access-control read test that forgot WithSecurity(...) cannot pass green.
            DemandStrictSecurityConfigured();

            // FetchXML is parsed into the equivalent QueryExpression (aggregates included) and runs through the same
            // checks and engine path, so the two forms of a query can never diverge.
            FetchQuery? parsedFetch = query is FetchExpression fetchExpression ? FetchXmlParser.Parse(fetchExpression.Query) : null;

            // No Read privilege on a table the query reads (root or any link-entity) fails the whole query.
            switch (query)
            {
                case QueryExpression qe:
                    DemandQueryReadPrivileges(qe);
                    break;
                case QueryByAttribute qba:
                    DemandQueryReadPrivileges(new QueryExpression(qba.EntityName));
                    break;
                case FetchExpression:
                    DemandQueryReadPrivileges(parsedFetch!.Query);
                    break;
            }

            // Field-level security: an aggregate/group-by over a field-secured column the caller can't read would
            // otherwise leak it through the aggregate value, so reject the query up front.
            DemandAggregateColumnReads(query);

            // With security on, the read filter is applied inside the engine to the primary rows BEFORE paging, so
            // pages are drawn from the readable set (not trimmed afterwards).
            InMemoryQueryEngine engine = new InMemoryQueryEngine(name => Table(name).Values, _currentUserId, ReadFilter(), _clock());
            EntityCollection result;
            switch (query)
            {
                case QueryExpression qe:
                    result = engine.ExecuteQueryExpression(qe);
                    break;
                case QueryByAttribute qba:
                    result = engine.ExecuteQueryByAttribute(qba);
                    break;
                case FetchExpression:
                    result = engine.ExecuteFetch(parsedFetch!);
                    break;
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService supports QueryExpression, QueryByAttribute and FetchExpression. " +
                        $"{query.GetType().Name} is not supported yet.");
            }

            // Field-level security masks field-secured columns the caller can't read from each returned record.
            if (Enforcing != null)
            {
                foreach (Entity row in result.Entities)
                {
                    MaskSecuredColumns(row);
                }
            }

            foreach (Entity row in result.Entities)
            {
                ShapeRead(row);
            }

            return result;
        }

        // Include support: fills a retrieved record's RelatedEntities from a RelationshipQueryCollection (1:N
        // children and N:1 parent). The relationship is resolved against the declared 1:N relationships by schema
        // name (the one given to WithRelationship), else by the (this-entity, other-entity) pair, mirroring how the
        // deep insert resolves relationships. Declare relationships with WithRelationship for this to resolve.
        private void PopulateRetrievedRelations(Entity entity, string entityName, Guid id, RelationshipQueryCollection related)
        {
            if (!Table(entityName).TryGetValue(id, out Entity? stored))
            {
                return;
            }

            foreach (KeyValuePair<Relationship, QueryBase> pair in related)
            {
                if (pair.Value is not QueryExpression childQuery || string.IsNullOrEmpty(childQuery.EntityName))
                {
                    continue;
                }

                string otherEntity = childQuery.EntityName;
                ColumnSet columns = childQuery.ColumnSet ?? new ColumnSet(true);

                // N:N: resolved by the real schema name (the registry / metadata key it directly). Walk the intersect
                // rows on this entity's side and retrieve the related records.
                if (TryResolveManyToMany(pair.Key.SchemaName, out RelationshipRegistry.ManyToMany m2m))
                {
                    (string callerAttribute, string relatedAttribute) = m2m.AttributesFor(entityName);
                    List<object> associatedIds = new List<object>();
                    foreach (Entity row in Table(m2m.IntersectEntity).Values)
                    {
                        if (EntityValues.AreEqual(EntityValues.GetValue(row, callerAttribute), id) &&
                            EntityValues.GetValue(row, relatedAttribute) is EntityReference relatedRef &&
                            Table(otherEntity).ContainsKey(relatedRef.Id))
                        {
                            associatedIds.Add(relatedRef.Id);
                        }
                    }

                    // Materialize the associated records through the same query path as RetrieveMultiple so the
                    // child query's Criteria/Orders/TopCount/paging are honoured (an empty set yields no rows).
                    QueryExpression associatedQuery = new QueryExpression(otherEntity) { ColumnSet = columns };
                    associatedQuery.Criteria.AddCondition(otherEntity + "id", ConditionOperator.In, associatedIds.ToArray());
                    ApplyChildQueryShape(associatedQuery, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(associatedQuery);
                    continue;
                }

                // 1:N: this entity is the parent (referenced) side -> children whose lookup points back at it.
                // Prefer an exact schema-name match (when two relationships link the same table pair), else the pair.
                List<OneToManyRegistry.OneToMany> childCandidates = _oneToMany.ChildRelationshipsOf(entityName)
                    .Where(r => string.Equals(r.ReferencingEntity, otherEntity, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                OneToManyRegistry.OneToMany? oneToMany =
                    childCandidates.FirstOrDefault(r => string.Equals(r.SchemaName, pair.Key.SchemaName, StringComparison.OrdinalIgnoreCase))
                    ?? childCandidates.FirstOrDefault();
                if (oneToMany != null)
                {
                    QueryExpression query = new QueryExpression(otherEntity) { ColumnSet = columns };
                    query.Criteria.AddCondition(oneToMany.ReferencingAttribute, ConditionOperator.Equal, id);
                    ApplyChildQueryShape(query, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(query);
                    continue;
                }

                // N:1: this entity is the child (referencing) side -> the single parent its lookup points at.
                // Prefer an exact schema-name match, else the table pair.
                List<OneToManyRegistry.OneToMany> parentCandidates = _oneToMany.ParentRelationshipsOf(entityName)
                    .Where(r => string.Equals(r.ReferencedEntity, otherEntity, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                OneToManyRegistry.OneToMany? manyToOne =
                    parentCandidates.FirstOrDefault(r => string.Equals(r.SchemaName, pair.Key.SchemaName, StringComparison.OrdinalIgnoreCase))
                    ?? parentCandidates.FirstOrDefault();
                if (manyToOne != null &&
                    stored.GetAttributeValue<EntityReference>(manyToOne.ReferencingAttribute) is EntityReference parentRef &&
                    Table(otherEntity).ContainsKey(parentRef.Id))
                {
                    // Run through the query path so the child query's Criteria/Orders/paging apply to the parent too
                    // (e.g. a Criteria that the parent fails to match filters it out, as it does on a real org).
                    QueryExpression parentQuery = new QueryExpression(otherEntity) { ColumnSet = columns };
                    parentQuery.Criteria.AddCondition(otherEntity + "id", ConditionOperator.Equal, parentRef.Id);
                    ApplyChildQueryShape(parentQuery, childQuery);
                    entity.RelatedEntities[pair.Key] = RetrieveMultiple(parentQuery);
                }
            }
        }

        // Copies a related-entities child query's shape - its Criteria (nested under the fake's own relationship
        // filter so the caller's query object is never mutated), Orders, TopCount, paging and Distinct - onto the
        // query the fake builds to materialize the relation, so Include honours the child query like a real org.
        private static void ApplyChildQueryShape(QueryExpression built, QueryExpression childQuery)
        {
            if (childQuery.Criteria != null &&
                (childQuery.Criteria.Conditions.Count > 0 || childQuery.Criteria.Filters.Count > 0))
            {
                built.Criteria.AddFilter(childQuery.Criteria);
            }

            built.Orders.AddRange(childQuery.Orders);
            built.TopCount = childQuery.TopCount;
            built.PageInfo = childQuery.PageInfo;
            built.Distinct = childQuery.Distinct;
        }
    }
}
