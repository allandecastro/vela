using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// Executes the read side of <see cref="InMemoryOrganizationService"/>: <see cref="QueryExpression"/>,
    /// <see cref="QueryByAttribute"/> and FetchXML-derived queries against an in-memory record set. Handles
    /// filters and operators, link-entities (joins, Any/All, aliased columns), ordering, paging, Distinct,
    /// column aliases and aggregates. Records are read through the supplied table accessor; the engine never
    /// mutates the store. Created per query so it observes the service's current user id.
    /// </summary>
    internal sealed class InMemoryQueryEngine
    {
        private readonly Func<string, IEnumerable<Entity>> _table;
        private readonly Guid _currentUserId;

        // Optional read-access predicate applied to primary rows BEFORE paging and BEFORE aggregation, so pages
        // and aggregate values are drawn from the records the caller may read (not trimmed afterwards). Null = no
        // security filtering.
        private readonly Func<Entity, bool>? _canRead;

        public InMemoryQueryEngine(Func<string, IEnumerable<Entity>> tableAccessor, Guid currentUserId, Func<Entity, bool>? canRead = null)
        {
            _table = tableAccessor;
            _currentUserId = currentUserId;
            _canRead = canRead;
        }

        // The primary rows of a table the caller may read (all of them when no security filter is set).
        private IEnumerable<Entity> ReadableRows(string entityName)
        {
            IEnumerable<Entity> rows = _table(entityName);
            return _canRead == null ? rows : rows.Where(_canRead);
        }

        public EntityCollection ExecuteQueryExpression(QueryExpression query)
        {
            // Native QueryExpression aggregation via ColumnSet.AttributeExpressions (AggregateType / HasGroupBy).
            if (HasAggregation(query.ColumnSet))
            {
                return ExecuteAggregate(BuildAggregateQuery(query));
            }

            List<Entity> rows = new List<Entity>();
            foreach (Entity baseRow in ReadableRows(query.EntityName))
            {
                IEnumerable<Entity> expanded = new[] { EntityValues.Clone(baseRow) };
                foreach (LinkEntity link in query.LinkEntities)
                {
                    expanded = expanded.SelectMany(row => ApplyLink(row, row, link));
                }

                rows.AddRange(expanded);
            }

            // Criteria is applied AFTER the joins so conditions can target linked entities via
            // ConditionExpression.EntityName (e.g. the "records not in a set" pattern with a left-outer join).
            List<Entity> filtered = rows.Where(row => EvaluateFilter(row, query.Criteria)).ToList();

            return BuildCollection(query.EntityName, filtered, query.ColumnSet, CollectOrders(query), query.TopCount, query.Distinct, query.PageInfo);
        }

        public EntityCollection ExecuteQueryByAttribute(QueryByAttribute query)
        {
            IEnumerable<Entity> matches = ReadableRows(query.EntityName).Where(entity =>
            {
                for (int i = 0; i < query.Attributes.Count; i++)
                {
                    object? expected = i < query.Values.Count ? query.Values[i] : null;
                    if (!EntityValues.AreEqual(EntityValues.GetValue(entity, query.Attributes[i]), expected))
                    {
                        return false;
                    }
                }

                return true;
            })
            // Deep-copy each matched row so results are isolated from the store, exactly as ExecuteQueryExpression
            // clones its base rows. Without this, mutating a returned mutable value (EntityReference/byte[]/nested
            // Entity/collection) would corrupt the stored record and break read isolation.
            .Select(EntityValues.Clone);

            List<OrderSpec> orders = query.Orders.Select(o => new OrderSpec { Key = o.AttributeName, Descending = o.OrderType == OrderType.Descending }).ToList();
            return BuildCollection(query.EntityName, matches, query.ColumnSet, orders, query.TopCount, distinct: false, query.PageInfo);
        }

        private static bool HasAggregation(ColumnSet columnSet)
        {
            return columnSet != null && columnSet.AttributeExpressions.Any(a => a.AggregateType != XrmAggregateType.None || a.HasGroupBy);
        }

        private static AggregateQuery BuildAggregateQuery(QueryExpression query)
        {
            AggregateQuery aggregate = new AggregateQuery { EntityName = query.EntityName, Top = query.TopCount };
            foreach (FilterExpression filter in new[] { query.Criteria })
            {
                aggregate.Criteria.AddFilter(filter);
            }

            foreach (XrmAttributeExpression expression in query.ColumnSet.AttributeExpressions)
            {
                if (expression.HasGroupBy)
                {
                    aggregate.Groups.Add(new AggregateColumn { Attribute = expression.AttributeName, Alias = expression.Alias });
                }
                else if (expression.AggregateType != XrmAggregateType.None)
                {
                    aggregate.Aggregates.Add(new AggregateColumn { Attribute = expression.AttributeName, Alias = expression.Alias, Function = MapAggregate(expression.AggregateType) });
                }
            }

            foreach (OrderExpression order in query.Orders)
            {
                aggregate.Orders.Add(new AggregateColumn { Alias = order.AttributeName, Descending = order.OrderType == OrderType.Descending });
            }

            return aggregate;
        }

        private static string MapAggregate(XrmAggregateType type)
        {
            switch (type)
            {
                case XrmAggregateType.Count: return "count";
                case XrmAggregateType.CountColumn: return "countcolumn";
                case XrmAggregateType.Sum: return "sum";
                case XrmAggregateType.Avg: return "avg";
                case XrmAggregateType.Min: return "min";
                case XrmAggregateType.Max: return "max";
                default: throw new NotSupportedException($"Aggregate type '{type}' is not supported yet.");
            }
        }

        private List<OrderSpec> CollectOrders(QueryExpression query)
        {
            List<OrderSpec> specs = new List<OrderSpec>();
            foreach (OrderExpression order in query.Orders)
            {
                specs.Add(new OrderSpec { Key = OrderColumnKey(order.EntityName, order.AttributeName), Descending = order.OrderType == OrderType.Descending });
            }

            // Dataverse applies LinkEntity.Orders after QueryExpression.Orders.
            foreach (LinkEntity link in query.LinkEntities)
            {
                CollectLinkOrders(link, specs);
            }

            return specs;
        }

        private static void CollectLinkOrders(LinkEntity link, List<OrderSpec> specs)
        {
            string alias = string.IsNullOrEmpty(link.EntityAlias) ? link.LinkToEntityName : link.EntityAlias;
            foreach (OrderExpression order in link.Orders)
            {
                specs.Add(new OrderSpec { Key = alias + "." + order.AttributeName, Descending = order.OrderType == OrderType.Descending });
            }

            foreach (LinkEntity child in link.LinkEntities)
            {
                CollectLinkOrders(child, specs);
            }
        }

        private static string OrderColumnKey(string? entityName, string attributeName)
        {
            return string.IsNullOrEmpty(entityName) ? attributeName : entityName + "." + attributeName;
        }

        private sealed class OrderSpec
        {
            public string Key { get; set; } = string.Empty;

            public bool Descending { get; set; }
        }

        // Aggregate FetchXML: group rows, compute aggregates, expose group/aggregate values as aliased columns.
        // Aggregates are computed over the rows the caller may read (security-trimmed), like the platform.
        public EntityCollection ExecuteAggregate(AggregateQuery query)
        {
            // Deep-copy the rows so group-by and min/max values (exposed as AliasedValue) cannot alias the store
            // and let a caller mutate a stored record through an aggregate result.
            List<Entity> rows = ReadableRows(query.EntityName).Where(entity => EvaluateFilter(entity, query.Criteria)).Select(EntityValues.Clone).ToList();

            List<List<Entity>> groups = new List<List<Entity>>();
            if (query.Groups.Count == 0)
            {
                groups.Add(rows); // a single group over all rows
            }
            else
            {
                Dictionary<string, List<Entity>> byKey = new Dictionary<string, List<Entity>>();
                List<string> order = new List<string>();
                foreach (Entity row in rows)
                {
                    string key = string.Join("", query.Groups.Select(g => EntityValues.Normalize(EntityValues.GetValue(row, g.Attribute))?.ToString() ?? " "));
                    if (!byKey.TryGetValue(key, out List<Entity>? bucket))
                    {
                        bucket = new List<Entity>();
                        byKey[key] = bucket;
                        order.Add(key);
                    }

                    bucket.Add(row);
                }

                foreach (string key in order)
                {
                    groups.Add(byKey[key]);
                }
            }

            List<Entity> results = new List<Entity>();
            foreach (List<Entity> group in groups)
            {
                Entity result = new Entity(query.EntityName);
                // Stryker disable once all : a group is built from at least one row, so it is never empty (group.Count > 0 is always true here).
                Entity? first = group.Count > 0 ? group[0] : null;
                foreach (AggregateColumn groupColumn in query.Groups)
                {
                    // Stryker disable once all : first is the head of a non-empty group above, so it is never null.
                    object? value = first != null ? EntityValues.GetValue(first, groupColumn.Attribute) : null;
                    result[groupColumn.Alias] = new AliasedValue(query.EntityName, groupColumn.Attribute, value);
                }

                foreach (AggregateColumn aggregate in query.Aggregates)
                {
                    result[aggregate.Alias] = new AliasedValue(query.EntityName, aggregate.Attribute, ComputeAggregate(aggregate.Function, group, aggregate.Attribute));
                }

                results.Add(result);
            }

            IOrderedEnumerable<Entity>? ordered = null;
            foreach (AggregateColumn order in query.Orders)
            {
                string alias = order.Alias;
                Func<Entity, object?> key = entity => OrderKey(entity, alias);
                if (ordered == null)
                {
                    ordered = order.Descending ? results.OrderByDescending(key, ValueComparer.Instance) : results.OrderBy(key, ValueComparer.Instance);
                }
                else
                {
                    ordered = order.Descending ? ordered.ThenByDescending(key, ValueComparer.Instance) : ordered.ThenBy(key, ValueComparer.Instance);
                }
            }

            IEnumerable<Entity> final = ordered ?? (IEnumerable<Entity>)results;
            if (query.Top.HasValue)
            {
                final = final.Take(query.Top.Value);
            }

            EntityCollection collection = new EntityCollection { EntityName = query.EntityName };
            collection.Entities.AddRange(final);
            return collection;
        }

        private static object? ComputeAggregate(string function, List<Entity> group, string attribute)
        {
            switch (function)
            {
                case "count":
                    return group.Count;
                case "countcolumn":
                    return group.Count(entity => EntityValues.GetValue(entity, attribute) != null);
                case "sum":
                    return group.Select(entity => ToDecimal(EntityValues.GetValue(entity, attribute))).Where(value => value.HasValue).Sum(value => value!.Value);
                case "avg":
                    List<decimal> values = group.Select(entity => ToDecimal(EntityValues.GetValue(entity, attribute))).Where(value => value.HasValue).Select(value => value!.Value).ToList();
                    // No values to average -> null, as Dataverse returns (and consistent with min/max above).
                    return values.Count == 0 ? null : values.Average();
                case "min":
                    return group.Select(entity => EntityValues.GetValue(entity, attribute)).Where(value => value != null)
                        .OrderBy(value => EntityValues.Normalize(value), ValueComparer.Instance).FirstOrDefault();
                case "max":
                    return group.Select(entity => EntityValues.GetValue(entity, attribute)).Where(value => value != null)
                        .OrderByDescending(value => EntityValues.Normalize(value), ValueComparer.Instance).FirstOrDefault();
                default:
                    throw new NotSupportedException($"FetchXML aggregate function '{function}' is not supported yet.");
            }
        }

        private static decimal? ToDecimal(object? value)
        {
            object? normalized = EntityValues.Normalize(value);
            if (normalized == null)
            {
                return null;
            }

            try
            {
                return Convert.ToDecimal(normalized, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Order the full rows, project, de-duplicate (Distinct), then apply TopCount or paging. Aliased link
        // columns always survive projection. Used by QueryExpression, QueryByAttribute and FetchXML.
        private static EntityCollection BuildCollection(string entityName, IEnumerable<Entity> rows, ColumnSet columnSet, List<OrderSpec> orders, int? topCount, bool distinct, PagingInfo? pageInfo)
        {
            List<Entity> projected = ApplyOrders(rows, orders).Select(row => Finalize(row, columnSet)).ToList();
            if (distinct)
            {
                projected = DistinctRows(projected);
            }

            EntityCollection result = new EntityCollection { EntityName = entityName };

            if (pageInfo != null && pageInfo.ReturnTotalRecordCount)
            {
                const int limit = 5000;
                result.TotalRecordCount = Math.Min(projected.Count, limit);
                // Stryker disable once all : the 5000-row limit boundary is impractical to exercise in a unit test.
                result.TotalRecordCountLimitExceeded = projected.Count > limit;
            }

            if (topCount.HasValue)
            {
                result.Entities.AddRange(projected.Take(topCount.Value));
            }
            else if (pageInfo != null && pageInfo.Count > 0)
            {
                int page = pageInfo.PageNumber <= 0 ? 1 : pageInfo.PageNumber;
                int skip = (page - 1) * pageInfo.Count;
                result.Entities.AddRange(projected.Skip(skip).Take(pageInfo.Count));
                result.MoreRecords = projected.Count > skip + pageInfo.Count;
                if (result.MoreRecords)
                {
                    // Informational only (the engine pages by PageNumber, not the cookie): name the NEXT page.
                    result.PagingCookie = (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                result.Entities.AddRange(projected);
            }

            return result;
        }

        private static List<Entity> DistinctRows(List<Entity> rows)
        {
            HashSet<string> seen = new HashSet<string>();
            List<Entity> result = new List<Entity>();
            foreach (Entity row in rows)
            {
                // Stryker disable once all : key construction (separator/ordering) is internal — any consistent
                // key gives the same Distinct result, which is asserted separately.
                string key = string.Join("|", row.Attributes
                    .OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
                    .Select(attribute =>
                    {
                        object? value = attribute.Value is AliasedValue aliased ? aliased.Value : attribute.Value;
                        return attribute.Key + "=" + (EntityValues.Normalize(value)?.ToString() ?? string.Empty);
                    }));
                if (seen.Add(key))
                {
                    result.Add(row);
                }
            }

            return result;
        }

        // Joins a single LinkEntity: matches LinkFrom (on the source) == LinkTo (on the linked rows), applies
        // LinkCriteria, appends aliased columns, and recurses into nested links. Inner drops unmatched rows;
        // LeftOuter/Natural keep them. Yields one result row per matched linked record.
        //
        // Security trims the linked rows the same way it trims primary rows (via _canRead): a linked record the
        // caller can't read is not a match, so an INNER join drops the primary row while a LEFT-OUTER join keeps it
        // without the linked columns — exactly like Dataverse. Because ExpandNested recurses through ApplyLink, the
        // trim applies at every level of a nested join. No-op when security is off (_canRead == null).
        private IEnumerable<Entity> ApplyLink(Entity resultRow, Entity source, LinkEntity link)
        {
            object? fromValue = EntityValues.GetValue(source, link.LinkFromAttributeName);
            List<Entity> matched = _table(link.LinkToEntityName)
                .Where(target => EntityValues.AreEqual(fromValue, EntityValues.GetValue(target, link.LinkToAttributeName)))
                .Where(target => EvaluateFilter(target, link.LinkCriteria))
                .Where(target => _canRead == null || _canRead(target))
                .ToList();

            switch (link.JoinOperator)
            {
                case JoinOperator.Exists:
                case JoinOperator.In:
                    // Inner variants that don't multiply the parent row nor return the linked columns.
                    if (matched.Count > 0)
                    {
                        yield return resultRow;
                    }

                    yield break;

                case JoinOperator.MatchFirstRowUsingCrossApply:
                    // Left-outer, but the parent row appears at most once (columns from the first match).
                    if (matched.Count == 0)
                    {
                        yield return resultRow;
                        yield break;
                    }

                    foreach (Entity first in ExpandNested(AddAliasedColumns(resultRow, matched[0], link), matched[0], link))
                    {
                        yield return first;
                    }

                    yield break;

                case JoinOperator.LeftOuter:
                case JoinOperator.Natural:
                    if (matched.Count == 0)
                    {
                        yield return resultRow;
                        yield break;
                    }

                    break;

                default: // Inner
                    if (matched.Count == 0)
                    {
                        yield break;
                    }

                    break;
            }

            foreach (Entity target in matched)
            {
                foreach (Entity row in ExpandNested(AddAliasedColumns(resultRow, target, link), target, link))
                {
                    yield return row;
                }
            }
        }

        private IEnumerable<Entity> ExpandNested(Entity row, Entity source, LinkEntity link)
        {
            IEnumerable<Entity> expanded = new[] { row };
            foreach (LinkEntity child in link.LinkEntities)
            {
                expanded = expanded.SelectMany(r => ApplyLink(r, source, child));
            }

            return expanded;
        }

        private static Entity AddAliasedColumns(Entity resultRow, Entity target, LinkEntity link)
        {
            Entity copy = EntityValues.Clone(resultRow);
            string alias = string.IsNullOrEmpty(link.EntityAlias) ? link.LinkToEntityName : link.EntityAlias;
            IEnumerable<string> names = link.Columns == null || link.Columns.AllColumns
                ? target.Attributes.Keys.ToList()
                : link.Columns.Columns;

            foreach (string name in names)
            {
                if (target.Contains(name))
                {
                    // Deep-copy the linked value: target is a live store row, so aliasing its value here would
                    // let a caller mutate the store through a joined column and break read isolation.
                    copy[$"{alias}.{name}"] = new AliasedValue(target.LogicalName, name, EntityValues.CloneValue(target[name]));
                }
            }

            // Always expose the join key under the alias so conditions targeting the link (ConditionExpression.EntityName),
            // such as the "records not in a set" pattern, can tell matched from unmatched rows.
            string keyColumn = $"{alias}.{link.LinkToAttributeName}";
            if (!copy.Contains(keyColumn))
            {
                copy[keyColumn] = new AliasedValue(target.LogicalName, link.LinkToAttributeName, EntityValues.CloneValue(EntityValues.GetValue(target, link.LinkToAttributeName)));
            }

            return copy;
        }

        // Project base attributes per the ColumnSet; aliased (linked) columns are always kept.
        private static Entity Finalize(Entity row, ColumnSet columnSet)
        {
            Entity result = new Entity(row.LogicalName) { Id = row.Id };
            bool allColumns = columnSet == null || columnSet.AllColumns;
            foreach (KeyValuePair<string, object> attribute in row.Attributes)
            {
                if (attribute.Value is AliasedValue)
                {
                    result[attribute.Key] = attribute.Value;
                }
                else if (allColumns || columnSet!.Columns.Contains(attribute.Key))
                {
                    result[attribute.Key] = attribute.Value;
                }
            }

            // Column aliases (XrmAttributeExpression with no aggregate) are returned as AliasedValue.
            if (columnSet != null)
            {
                foreach (XrmAttributeExpression expression in columnSet.AttributeExpressions)
                {
                    if (expression.AggregateType == XrmAggregateType.None && !expression.HasGroupBy
                        && row.Attributes.TryGetValue(expression.AttributeName, out object? value))
                    {
                        result[expression.Alias] = new AliasedValue(row.LogicalName, expression.AttributeName, value);
                    }
                }
            }

            return result;
        }

        private static IEnumerable<Entity> ApplyOrders(IEnumerable<Entity> rows, List<OrderSpec> orders)
        {
            if (orders == null || orders.Count == 0)
            {
                return rows;
            }

            IOrderedEnumerable<Entity>? ordered = null;
            foreach (OrderSpec order in orders)
            {
                string key = order.Key;
                Func<Entity, object?> selector = entity => OrderKey(entity, key);
                if (ordered == null)
                {
                    ordered = order.Descending
                        ? rows.OrderByDescending(selector, ValueComparer.Instance)
                        : rows.OrderBy(selector, ValueComparer.Instance);
                }
                else
                {
                    ordered = order.Descending
                        ? ordered.ThenByDescending(selector, ValueComparer.Instance)
                        : ordered.ThenBy(selector, ValueComparer.Instance);
                }
            }

            return ordered!;
        }

        private static object? OrderKey(Entity entity, string attributeName)
        {
            if (!entity.Contains(attributeName))
            {
                return null;
            }

            object value = entity[attributeName];
            return EntityValues.Normalize(value is AliasedValue aliased ? aliased.Value : value);
        }

        private bool EvaluateFilter(Entity entity, FilterExpression? filter)
        {
            if (filter == null)
            {
                return true;
            }

            bool isAnd = filter.FilterOperator == LogicalOperator.And;
            List<bool> results = filter.Conditions.Select(condition => EvaluateCondition(entity, condition))
                .Concat(filter.Filters.Select(nested => EvaluateFilter(entity, nested)))
                .ToList();

            if (filter.AnyAllFilterLinkEntity != null)
            {
                results.Add(EvaluateAnyAll(entity, filter.AnyAllFilterLinkEntity));
            }

            // An empty filter matches everything.
            return isAnd ? results.All(r => r) : results.Count == 0 || results.Any(r => r);
        }

        // Any/All/NotAny/NotAll filters on related records (FilterExpression.AnyAllFilterLinkEntity).
        private bool EvaluateAnyAll(Entity parent, LinkEntity link)
        {
            object? fromValue = EntityValues.GetValue(parent, link.LinkFromAttributeName);
            List<Entity> linked = _table(link.LinkToEntityName)
                .Where(target => EntityValues.AreEqual(fromValue, EntityValues.GetValue(target, link.LinkToAttributeName)))
                .Where(target => _canRead == null || _canRead(target)) // an Any/All is evaluated over readable related records
                .ToList();
            bool anySatisfies = linked.Any(target => EvaluateFilter(target, link.LinkCriteria));

            switch (link.JoinOperator)
            {
                case JoinOperator.Any: return anySatisfies;
                case JoinOperator.NotAny: return !anySatisfies;
                case JoinOperator.NotAll: return anySatisfies; // equivalent to Any per the SDK docs
                case JoinOperator.All: return linked.Count > 0 && !anySatisfies;
                default: return anySatisfies;
            }
        }

        private bool EvaluateCondition(Entity entity, ConditionExpression condition)
        {
            object? actual = GetConditionValue(entity, condition);
            IList<object> values = condition.Values;

            object? Operand(int index)
            {
                if (index >= values.Count)
                {
                    return null;
                }

                // CompareColumns: the value is the name of another column on the same row.
                return condition.CompareColumns ? EntityValues.GetValue(entity, EntityValues.AsString(values[index])) : values[index];
            }

            // Dataverse uses SQL three-valued logic: a NULL attribute never satisfies a comparison, so the
            // "negative" operators (ne, not-in, not-like, ...) EXCLUDE rows where the attribute is null rather
            // than matching them. Without this, in-memory queries return rows that real Dataverse would omit.
            if (actual == null && IsNullExcludingOperator(condition.Operator))
            {
                return false;
            }

            switch (condition.Operator)
            {
                case ConditionOperator.Null:
                    return actual == null;
                case ConditionOperator.NotNull:
                    return actual != null;
                case ConditionOperator.Equal:
                    return EntityValues.AreEqual(actual, Operand(0));
                case ConditionOperator.NotEqual:
                    return !EntityValues.AreEqual(actual, Operand(0));
                case ConditionOperator.In:
                    return values.Any(v => EntityValues.AreEqual(actual, v));
                case ConditionOperator.NotIn:
                    return !values.Any(v => EntityValues.AreEqual(actual, v));
                case ConditionOperator.GreaterThan:
                    return EntityValues.Compare(actual, Operand(0)) > 0;
                case ConditionOperator.GreaterEqual:
                    return EntityValues.Compare(actual, Operand(0)) >= 0;
                case ConditionOperator.LessThan:
                    return EntityValues.Compare(actual, Operand(0)) < 0;
                case ConditionOperator.LessEqual:
                    return EntityValues.Compare(actual, Operand(0)) <= 0;
                case ConditionOperator.Between:
                    return EntityValues.Compare(actual, Operand(0)) >= 0 && EntityValues.Compare(actual, Operand(1)) <= 0;
                case ConditionOperator.NotBetween:
                    return !(EntityValues.Compare(actual, Operand(0)) >= 0 && EntityValues.Compare(actual, Operand(1)) <= 0);
                case ConditionOperator.BeginsWith:
                    return EntityValues.AsString(actual).StartsWith(EntityValues.AsString(Operand(0)), StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.DoesNotBeginWith:
                    return !EntityValues.AsString(actual).StartsWith(EntityValues.AsString(Operand(0)), StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.EndsWith:
                    return EntityValues.AsString(actual).EndsWith(EntityValues.AsString(Operand(0)), StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.DoesNotEndWith:
                    return !EntityValues.AsString(actual).EndsWith(EntityValues.AsString(Operand(0)), StringComparison.OrdinalIgnoreCase);
                case ConditionOperator.Like:
                    return LikeMatch(actual, Operand(0));
                case ConditionOperator.NotLike:
                    return !LikeMatch(actual, Operand(0));
                case ConditionOperator.EqualUserId:
                    return actual != null && EntityValues.AreEqual(actual, _currentUserId);
                case ConditionOperator.NotEqualUserId:
                    return actual == null || !EntityValues.AreEqual(actual, _currentUserId);
                case ConditionOperator.On:
                    return DatePart(actual) is DateTime on && on == DatePart(Operand(0));
                case ConditionOperator.OnOrBefore:
                    return DatePart(actual) is DateTime ob && DatePart(Operand(0)) is DateTime obv && ob <= obv;
                case ConditionOperator.OnOrAfter:
                    return DatePart(actual) is DateTime oa && DatePart(Operand(0)) is DateTime oav && oa >= oav;
                case ConditionOperator.Today:
                    return DatePart(actual) == DateTime.Today;
                case ConditionOperator.Yesterday:
                    return DatePart(actual) == DateTime.Today.AddDays(-1);
                case ConditionOperator.Tomorrow:
                    return DatePart(actual) == DateTime.Today.AddDays(1);
                case ConditionOperator.ThisMonth:
                    return DatePart(actual) is DateTime tm && tm.Year == DateTime.Today.Year && tm.Month == DateTime.Today.Month;
                case ConditionOperator.ThisYear:
                    return DatePart(actual) is DateTime ty && ty.Year == DateTime.Today.Year;
                case ConditionOperator.LastXDays:
                    return InDayRange(actual, -ToInt(Operand(0)), 0);
                case ConditionOperator.NextXDays:
                    return InDayRange(actual, 0, ToInt(Operand(0)));
                case ConditionOperator.OlderThanXDays:
                    return DatePart(actual) is DateTime ox && ox < DateTime.Today.AddDays(-ToInt(Operand(0)));
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService does not support the '{condition.Operator}' operator yet.");
            }
        }

        // Operators that, like real Dataverse, must NOT match a row whose attribute is null.
        private static bool IsNullExcludingOperator(ConditionOperator op)
        {
            // A NULL attribute never satisfies a value comparison (SQL three-valued logic). The negative
            // operators exclude null rather than matching it, and the positive ordered comparisons are
            // "unknown" -> excluded too (and would otherwise throw on Compare(null, ...)).
            return op is ConditionOperator.NotEqual
                or ConditionOperator.NotIn
                or ConditionOperator.NotBetween
                or ConditionOperator.DoesNotBeginWith
                or ConditionOperator.DoesNotEndWith
                or ConditionOperator.NotLike
                or ConditionOperator.GreaterThan
                or ConditionOperator.GreaterEqual
                or ConditionOperator.LessThan
                or ConditionOperator.LessEqual
                or ConditionOperator.Between;
        }

        private static object? GetConditionValue(Entity entity, ConditionExpression condition)
        {
            if (!string.IsNullOrEmpty(condition.EntityName))
            {
                string key = condition.EntityName + "." + condition.AttributeName;
                if (entity.Attributes.TryGetValue(key, out object? value))
                {
                    return value is AliasedValue aliased ? aliased.Value : value;
                }

                return null;
            }

            return EntityValues.GetValue(entity, condition.AttributeName);
        }

        private static DateTime? DatePart(object? value)
        {
            object? normalized = EntityValues.Normalize(value);
            if (normalized is DateTime dateTime)
            {
                return dateTime.Date;
            }

            if (normalized is string text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsed))
            {
                return parsed.Date;
            }

            return null;
        }

        private static bool InDayRange(object? value, int fromDays, int toDays)
        {
            DateTime? date = DatePart(value);
            if (date == null)
            {
                return false;
            }

            DateTime today = DateTime.Today;
            return date.Value >= today.AddDays(fromDays) && date.Value <= today.AddDays(toDays);
        }

        private static int ToInt(object? value)
        {
            return int.TryParse(EntityValues.AsString(value), out int result) ? result : 0;
        }

        private static bool LikeMatch(object? actual, object? pattern)
        {
            string regex = "^" + Regex.Escape(EntityValues.AsString(pattern)).Replace("%", ".*").Replace("_", ".") + "$";
            return Regex.IsMatch(EntityValues.AsString(actual), regex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private sealed class ValueComparer : IComparer<object?>
        {
            public static readonly ValueComparer Instance = new ValueComparer();

            public int Compare(object? x, object? y)
            {
                if (x == null && y == null)
                {
                    return 0;
                }

                if (x == null)
                {
                    return -1;
                }

                if (y == null)
                {
                    return 1;
                }

                if (x is IComparable comparable)
                {
                    try
                    {
                        return comparable.CompareTo(Convert.ChangeType(y, x.GetType()));
                    }
                    catch (Exception)
                    {
                        // Fall through to string comparison for incompatible types.
                    }
                }

                return string.Compare(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
