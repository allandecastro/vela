using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
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

        // "Today" for the relative date operators (Today/Yesterday/LastXDays/…), taken from the service's injected
        // clock so those operators are deterministic and controllable in tests, not tied to the host's local date.
        private readonly DateTime _today;

        public InMemoryQueryEngine(Func<string, IEnumerable<Entity>> tableAccessor, Guid currentUserId, Func<Entity, bool>? canRead = null, DateTime? now = null)
        {
            _table = tableAccessor;
            _currentUserId = currentUserId;
            _canRead = canRead;
            _today = (now ?? DateTime.UtcNow).Date;
        }

        // The primary rows of a table the caller may read (all of them when no security filter is set).
        private IEnumerable<Entity> ReadableRows(string entityName)
        {
            IEnumerable<Entity> rows = _table(entityName);
            return _canRead == null ? rows : rows.Where(_canRead);
        }

        // A parsed FetchXML query runs through the exact QueryExpression path; it only adds the FetchXML-only
        // aggregate options (distinct countcolumn, aggregatelimit) a QueryExpression cannot carry.
        public EntityCollection ExecuteFetch(FetchQuery fetch) => ExecuteQueryExpression(fetch.Query, fetch);

        public EntityCollection ExecuteQueryExpression(QueryExpression query, FetchQuery? fetch = null)
        {
            if (!string.IsNullOrEmpty(query.DataSource))
            {
                throw new NotSupportedException(
                    $"InMemoryOrganizationService does not support the '{query.DataSource}' data source (long-term retained data).");
            }

            // Native QueryExpression aggregation via ColumnSet.AttributeExpressions (AggregateType / HasGroupBy) on
            // the root or on any link-entity.
            if (HasAggregation(query))
            {
                return ExecuteAggregate(query, fetch);
            }

            List<Entity> filtered = JoinedRows(query);
            return BuildCollection(query.EntityName, filtered, query.ColumnSet, CollectOrders(query), query.TopCount, query.Distinct, query.PageInfo);
        }

        // The readable primary rows expanded through every link-entity, then filtered by Criteria. Criteria is
        // applied AFTER the joins so conditions can target linked entities via ConditionExpression.EntityName
        // (e.g. the "records not in a set" pattern with a left-outer join).
        private List<Entity> JoinedRows(QueryExpression query)
        {
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

            return rows.Where(row => EvaluateFilter(row, query.Criteria)).ToList();
        }
        public EntityCollection ExecuteQueryByAttribute(QueryByAttribute query)
        {
            IEnumerable<Entity> matches = ReadableRows(query.EntityName).Where(entity =>
            {
                for (int i = 0; i < query.Attributes.Count; i++)
                {
                    object? expected = i < query.Values.Count ? query.Values[i] : null;
                    if (!EntityValues.ConditionEquals(EntityValues.GetValue(entity, query.Attributes[i]), expected))
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

        private static bool HasAggregation(QueryExpression query)
        {
            return IsAggregation(query.ColumnSet) || query.LinkEntities.Any(HasAggregation);
        }

        private static bool HasAggregation(LinkEntity link)
        {
            return IsAggregation(link.Columns) || link.LinkEntities.Any(HasAggregation);
        }

        private static bool IsAggregation(ColumnSet? columnSet)
        {
            return columnSet != null && columnSet.AttributeExpressions.Any(a => a.AggregateType != XrmAggregateType.None || a.HasGroupBy);
        }

        /// <summary>
        /// Every (table, column) a query reads through a <see cref="XrmAttributeExpression"/> (aggregate, group-by or
        /// column alias) on the root and on every link-entity, for the field-level-security pre-check.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, string>> AttributeExpressionColumns(QueryExpression query)
        {
            List<KeyValuePair<string, string>> columns = new List<KeyValuePair<string, string>>();
            AddExpressionColumns(columns, query.EntityName, query.ColumnSet);
            foreach (LinkEntity link in query.LinkEntities)
            {
                AddExpressionColumns(columns, link);
            }

            return columns;
        }

        private static void AddExpressionColumns(List<KeyValuePair<string, string>> columns, LinkEntity link)
        {
            AddExpressionColumns(columns, link.LinkToEntityName, link.Columns);
            foreach (LinkEntity child in link.LinkEntities)
            {
                AddExpressionColumns(columns, child);
            }
        }

        private static void AddExpressionColumns(List<KeyValuePair<string, string>> columns, string entityName, ColumnSet? columnSet)
        {
            foreach (XrmAttributeExpression expression in columnSet?.AttributeExpressions ?? Enumerable.Empty<XrmAttributeExpression>())
            {
                columns.Add(new KeyValuePair<string, string>(entityName, expression.AttributeName));
            }
        }

        private static AggregateQuery BuildAggregateQuery(QueryExpression query, FetchQuery? fetch)
        {
            AggregateQuery aggregate = new AggregateQuery { EntityName = query.EntityName };
            AddAggregateColumns(aggregate, query.ColumnSet, null, query.EntityName, fetch);
            foreach (LinkEntity link in query.LinkEntities)
            {
                AddLinkAggregateColumns(aggregate, link, fetch);
            }

            foreach (OrderExpression order in query.Orders)
            {
                // An aggregate result is ordered by a result alias (OrderExpression.Alias, or the AttributeName when
                // it names an alias). Anything else would order by a column the result doesn't carry.
                string key = string.IsNullOrEmpty(order.Alias) ? order.AttributeName : order.Alias;
                if (!aggregate.Groups.Concat(aggregate.Aggregates).Any(column => column.Alias == key))
                {
                    throw new NotSupportedException(
                        $"An aggregate query must order by a group-by or aggregate alias; '{key}' is not one.");
                }

                aggregate.Orders.Add(new AggregateColumn { Alias = key, Descending = order.OrderType == OrderType.Descending });
            }

            return aggregate;
        }

        private static void AddLinkAggregateColumns(AggregateQuery aggregate, LinkEntity link, FetchQuery? fetch)
        {
            if (link.Orders.Count > 0)
            {
                throw new NotSupportedException("An aggregate query cannot order a link-entity; order by a result alias on the root.");
            }

            string alias = string.IsNullOrEmpty(link.EntityAlias) ? link.LinkToEntityName : link.EntityAlias;
            AddAggregateColumns(aggregate, link.Columns, alias, link.LinkToEntityName, fetch);
            foreach (LinkEntity child in link.LinkEntities)
            {
                AddLinkAggregateColumns(aggregate, child, fetch);
            }
        }

        // entityAlias is null for the root, else the link alias the joined row keys the linked columns under.
        private static void AddAggregateColumns(AggregateQuery aggregate, ColumnSet? columnSet, string? entityAlias, string entityName, FetchQuery? fetch)
        {
            if (columnSet == null)
            {
                return;
            }

            if (columnSet.AllColumns || columnSet.Columns.Count > 0)
            {
                throw new NotSupportedException(
                    $"An aggregate query can only select group-by and aggregate columns; '{entityName}' also selects plain columns.");
            }

            foreach (XrmAttributeExpression expression in columnSet.AttributeExpressions)
            {
                if (string.IsNullOrEmpty(expression.Alias))
                {
                    throw new NotSupportedException($"The aggregate column '{expression.AttributeName}' requires an alias.");
                }

                AggregateColumn column = new AggregateColumn
                {
                    Attribute = expression.AttributeName,
                    Alias = expression.Alias,
                    EntityName = entityName,
                    Key = entityAlias == null ? expression.AttributeName : entityAlias + "." + expression.AttributeName
                };

                if (expression.HasGroupBy)
                {
                    column.DateGrouping = CheckDateGrouping(expression.DateTimeGrouping);
                    aggregate.Groups.Add(column);
                }
                else if (expression.AggregateType != XrmAggregateType.None)
                {
                    column.Function = MapAggregate(expression.AggregateType);
                    column.Distinct = fetch != null && fetch.DistinctAggregateAliases.Contains(expression.Alias);
                    aggregate.Aggregates.Add(column);
                }
                else
                {
                    throw new NotSupportedException(
                        $"An aggregate query column must be a group-by or an aggregate; '{expression.Alias}' is neither.");
                }
            }
        }

        // Calendar date groupings are computed from the stored (UTC) value. Week and fiscal groupings depend on
        // per-organization calendar settings the harness doesn't model, so they are rejected rather than guessed.
        private static XrmDateTimeGrouping CheckDateGrouping(XrmDateTimeGrouping grouping)
        {
            switch (grouping)
            {
                case XrmDateTimeGrouping.None:
                case XrmDateTimeGrouping.Day:
                case XrmDateTimeGrouping.Month:
                case XrmDateTimeGrouping.Quarter:
                case XrmDateTimeGrouping.Year:
                    return grouping;
                default:
                    throw new NotSupportedException($"InMemoryOrganizationService does not support the '{grouping}' date grouping.");
            }
        }

        private static object? DateGroupValue(object? value, XrmDateTimeGrouping grouping)
        {
            if (grouping == XrmDateTimeGrouping.None || value == null)
            {
                return value;
            }

            if (!(EntityValues.Normalize(value) is DateTime date))
            {
                throw new NotSupportedException("A date grouping can only group a date/time column.");
            }

            switch (grouping)
            {
                case XrmDateTimeGrouping.Day: return date.Day;
                case XrmDateTimeGrouping.Month: return date.Month;
                case XrmDateTimeGrouping.Quarter: return ((date.Month - 1) / 3) + 1;
                default: return date.Year; // Year (CheckDateGrouping admits no other)
            }
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

        // A group-by/aggregate query (group-by + aggregate + order columns), built from the QueryExpression.
        private sealed class AggregateQuery
        {
            public string EntityName { get; set; } = string.Empty;

            public List<AggregateColumn> Groups { get; } = new List<AggregateColumn>();

            public List<AggregateColumn> Aggregates { get; } = new List<AggregateColumn>();

            public List<AggregateColumn> Orders { get; } = new List<AggregateColumn>();
        }

        private sealed class AggregateColumn
        {
            public string Attribute { get; set; } = string.Empty;

            public string Alias { get; set; } = string.Empty;

            // The logical table the column belongs to (root or linked), reported on the AliasedValue.
            public string EntityName { get; set; } = string.Empty;

            // Where the joined row exposes the value: the attribute (root) or "{linkAlias}.{attribute}".
            public string Key { get; set; } = string.Empty;

            public string Function { get; set; } = string.Empty;

            public bool Distinct { get; set; }

            public XrmDateTimeGrouping DateGrouping { get; set; }

            public bool Descending { get; set; }
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

        // The platform's default AggregateQueryRecordLimit.
        private const int DefaultAggregateLimit = 50000;

        // Group the joined + filtered rows, compute the aggregates, expose group/aggregate values as aliased
        // columns. Aggregates are computed over the rows the caller may read (security-trimmed), like the platform,
        // and over the JOINED rows, so link-entities filter (inner), multiply (1:N) and feed linked columns exactly as
        // they do for a plain query.
        private EntityCollection ExecuteAggregate(QueryExpression query, FetchQuery? fetch)
        {
            AggregateQuery aggregate = BuildAggregateQuery(query, fetch);

            // Rows are deep copies (Clone) and linked values are cloned on output, so group-by and min/max values
            // (exposed as AliasedValue) cannot alias the store.
            List<Entity> rows = JoinedRows(query);
            int limit = fetch?.AggregateLimit ?? DefaultAggregateLimit;
            if (rows.Count > limit)
            {
                throw new FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = AggregateQueryRecordLimitExceeded, Message = "AggregateQueryRecordLimit exceeded. Cannot perform this operation." },
                    new FaultReason("AggregateQueryRecordLimit exceeded. Cannot perform this operation."));
            }

            List<List<Entity>> groups = new List<List<Entity>>();
            if (aggregate.Groups.Count == 0)
            {
                groups.Add(rows); // a single group over all rows
            }
            else
            {
                Dictionary<string, List<Entity>> byKey = new Dictionary<string, List<Entity>>();
                List<string> order = new List<string>();
                foreach (Entity row in rows)
                {
                    // Delimit fields with a unit separator (never present in data) and qualify each with its
                    // alias, so distinct tuples cannot collide: without a separator (city:"New", region:"York")
                    // and (city:"NewYork",region:"") both keyed to "NewYork" and merged into one group with wrong
                    // aggregate/count values.
                    string key = string.Join("\u001f", aggregate.Groups.Select(g => $"{g.Alias}={EntityValues.Normalize(GroupValue(row, g))?.ToString() ?? " "}"));
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
                Entity result = new Entity(aggregate.EntityName);
                foreach (AggregateColumn groupColumn in aggregate.Groups)
                {
                    // A group is built from at least one row, so group[0] exists whenever there are group columns.
                    result[groupColumn.Alias] = new AliasedValue(groupColumn.EntityName, groupColumn.Attribute, EntityValues.CloneValue(GroupValue(group[0], groupColumn)));
                }

                foreach (AggregateColumn column in aggregate.Aggregates)
                {
                    result[column.Alias] = new AliasedValue(column.EntityName, column.Attribute, EntityValues.CloneValue(ComputeAggregate(column, group)));
                }

                results.Add(result);
            }

            IOrderedEnumerable<Entity>? ordered = null;
            foreach (AggregateColumn order in aggregate.Orders)
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

            EntityCollection collection = new EntityCollection { EntityName = aggregate.EntityName };
            Page(collection, (ordered ?? (IEnumerable<Entity>)results).ToList(), query.TopCount, query.PageInfo);
            return collection;
        }

        // AggregateQueryRecordLimit exceeded (0x8004E023).
        private const int AggregateQueryRecordLimitExceeded = unchecked((int)0x8004E023);

        private static object? GroupValue(Entity row, AggregateColumn column)
        {
            return DateGroupValue(RowValue(row, column.Key), column.DateGrouping);
        }

        private static object? ComputeAggregate(AggregateColumn column, List<Entity> group)
        {
            List<object> values = group.Select(entity => RowValue(entity, column.Key)).Where(value => value != null).Select(value => value!).ToList();
            switch (column.Function)
            {
                case "count":
                    return group.Count;
                case "countcolumn":
                    if (!column.Distinct)
                    {
                        return values.Count;
                    }

                    // distinct='true': the number of unique non-null values (compared like a filter: case-insensitive).
                    List<object> unique = new List<object>();
                    foreach (object value in values)
                    {
                        if (!unique.Any(seen => EntityValues.AreEqual(seen, value)))
                        {
                            unique.Add(value);
                        }
                    }

                    return unique.Count;
                case "sum":
                    List<decimal> sumValues = values.Select(ToDecimal).Where(value => value.HasValue).Select(value => value!.Value).ToList();
                    // No values to sum -> null, as Dataverse returns for an aggregate over no (non-null) rows
                    // (consistent with avg/min/max below), rather than 0 which would be indistinguishable from a real 0.
                    return sumValues.Count == 0 ? (object?)null : sumValues.Sum();
                case "avg":
                    List<decimal> avgValues = values.Select(ToDecimal).Where(value => value.HasValue).Select(value => value!.Value).ToList();
                    // No values to average -> null, as Dataverse returns (and consistent with min/max above).
                    return avgValues.Count == 0 ? null : avgValues.Average();
                case "min":
                    return values.OrderBy(value => EntityValues.Normalize(value), ValueComparer.Instance).FirstOrDefault();
                default: // max (MapAggregate admits no other function)
                    return values.OrderByDescending(value => EntityValues.Normalize(value), ValueComparer.Instance).FirstOrDefault();
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
            Page(result, projected, topCount, pageInfo);
            return result;
        }

        // TopCount, else paging (PageNumber/Count, MoreRecords, informational cookie), plus the total record count.
        // Shared by plain and aggregate queries.
        private static void Page(EntityCollection result, List<Entity> projected, int? topCount, PagingInfo? pageInfo)
        {
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
        // LeftOuter keeps them. Yields one result row per matched linked record.
        //
        // Security trims the linked rows the same way it trims primary rows (via _canRead): a linked record the
        // caller can't read is not a match, so an INNER join drops the primary row while a LEFT-OUTER join keeps it
        // without the linked columns — exactly like Dataverse. Because ExpandNested recurses through ApplyLink, the
        // trim applies at every level of a nested join. No-op when security is off (_canRead == null).
        private IEnumerable<Entity> ApplyLink(Entity resultRow, Entity source, LinkEntity link)
        {
            switch (link.JoinOperator)
            {
                case JoinOperator.Any:
                case JoinOperator.NotAny:
                case JoinOperator.All:
                case JoinOperator.NotAll:
                    // A related-record filter used as a join: it keeps or drops the parent row, never multiplies it
                    // nor returns linked columns (same semantics as FilterExpression.AnyAllFilterLinkEntity).
                    if (EvaluateAnyAll(source, link))
                    {
                        yield return resultRow;
                    }

                    yield break;

                case JoinOperator.Natural:
                    throw new NotSupportedException("InMemoryOrganizationService does not support the 'Natural' join operator.");
            }

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
                    // Inner variants that don't multiply the parent row nor return the linked columns. A nested
                    // link restricts which linked rows count as a match.
                    if (matched.Any(target => NestedMatch(target, link)))
                    {
                        yield return resultRow;
                    }

                    yield break;

                case JoinOperator.MatchFirstRowUsingCrossApply:
                    // An inner variant (CROSS APPLY): an unmatched parent is dropped; a matched one appears once,
                    // with the columns of its first matching linked row.
                    Entity? first = matched.FirstOrDefault(target => NestedMatch(target, link));
                    if (first == null)
                    {
                        yield break;
                    }

                    foreach (Entity row in ExpandNested(AddAliasedColumns(resultRow, first, link), first, link).Take(1))
                    {
                        yield return row;
                    }

                    yield break;

                case JoinOperator.LeftOuter:
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

        // True when a linked row survives the link's own nested link-entities (an inner/exists child with no match
        // rejects it; an outer child never does). Used by the joins that only test for a match.
        private bool NestedMatch(Entity target, LinkEntity link)
        {
            return ExpandNested(target, target, link).Any();
        }

        // The joined row keeps a reference to every linked record under this hidden key, so conditions
        // (ConditionExpression.EntityName), column comparisons, link orders and aggregates can read ANY column of
        // the linked row — not only the selected ones. Finalize strips it, so it never reaches the caller.
        private const string LinkedRecordPrefix = "\u0001link:";

        private sealed class LinkedRecord
        {
            public LinkedRecord(Entity target)
            {
                Target = target;
            }

            public Entity Target { get; }
        }

        private static Entity AddAliasedColumns(Entity resultRow, Entity target, LinkEntity link)
        {
            Entity copy = EntityValues.Clone(resultRow);
            string alias = string.IsNullOrEmpty(link.EntityAlias) ? link.LinkToEntityName : link.EntityAlias;
            copy[LinkedRecordPrefix + alias] = new LinkedRecord(target);

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

            // Column aliases on the link (<attribute name alias/> in a link-entity) are returned under the alias.
            foreach (XrmAttributeExpression expression in link.Columns?.AttributeExpressions ?? Enumerable.Empty<XrmAttributeExpression>())
            {
                object? value = EntityValues.GetValue(target, expression.AttributeName);
                if (value != null)
                {
                    copy[expression.Alias] = new AliasedValue(target.LogicalName, expression.AttributeName, EntityValues.CloneValue(value));
                }
            }

            return copy;
        }

        // Reads a value from a (possibly joined) row: a root column by name, or a linked column as
        // "{linkAlias}.{column}" — from the projected aliased column or, when not selected, the linked record itself.
        private static object? RowValue(Entity row, string key)
        {
            if (row.Attributes.TryGetValue(key, out object? value))
            {
                return value is AliasedValue aliased ? aliased.Value : value;
            }

            int dot = key.IndexOf('.');
            if (dot != -1 && row.Attributes.TryGetValue(LinkedRecordPrefix + key.Substring(0, dot), out object? linked))
            {
                return EntityValues.GetValue(((LinkedRecord)linked).Target, key.Substring(dot + 1));
            }

            return EntityValues.GetValue(row, key);
        }

        // Project base attributes per the ColumnSet; aliased (linked) columns are always kept.
        private static Entity Finalize(Entity row, ColumnSet columnSet)
        {
            Entity result = new Entity(row.LogicalName) { Id = row.Id };
            bool allColumns = columnSet == null || columnSet.AllColumns;
            foreach (KeyValuePair<string, object> attribute in row.Attributes)
            {
                if (attribute.Value is LinkedRecord)
                {
                    continue; // internal join state, never returned
                }

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
                    object? value = EntityValues.GetValue(row, expression.AttributeName);
                    if (value != null)
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
            return EntityValues.Normalize(RowValue(entity, attributeName));
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

        // Any/All/NotAny/NotAll filters on related records (FilterExpression.AnyAllFilterLinkEntity). A related
        // record "satisfies" the link when it passes LinkCriteria and its nested link-entities.
        private bool EvaluateAnyAll(Entity parent, LinkEntity link)
        {
            object? fromValue = EntityValues.GetValue(parent, link.LinkFromAttributeName);
            List<Entity> linked = _table(link.LinkToEntityName)
                .Where(target => EntityValues.AreEqual(fromValue, EntityValues.GetValue(target, link.LinkToAttributeName)))
                .Where(target => _canRead == null || _canRead(target)) // an Any/All is evaluated over readable related records
                .ToList();
            bool anySatisfies = linked.Any(target => EvaluateFilter(target, link.LinkCriteria) && NestedMatch(target, link));

            switch (link.JoinOperator)
            {
                case JoinOperator.NotAny: return !anySatisfies;
                case JoinOperator.All: return linked.Count > 0 && !anySatisfies;
                default: return anySatisfies; // Any, and NotAll (equivalent to Any per the SDK docs)
            }
        }

        private bool EvaluateCondition(Entity entity, ConditionExpression condition)
        {
            object? actual = GetConditionValue(entity, condition);
            IList<object> values = condition.Values;

            if (condition.CompareColumns)
            {
                return EvaluateColumnComparison(entity, condition, actual);
            }

            object? Operand(int index)
            {
                return index >= values.Count ? null : values[index];
            }

            // Dataverse uses SQL three-valued logic: a NULL attribute never satisfies a comparison, so the
            // "negative" operators (ne, not-in, not-like, ...) EXCLUDE rows where the attribute is null rather
            // than matching them. Without this, in-memory queries return rows that real Dataverse would omit.
            if (actual == null && IsNullExcludingOperator(condition.Operator))
            {
                return false;
            }

            // SQL three-valued logic also makes an ordered comparison "unknown" when the operand it compares against
            // is null (for Between, either bound), so the row is excluded rather than throwing inside
            // EntityValues.Compare(actual, null).
            switch (condition.Operator)
            {
                case ConditionOperator.GreaterThan:
                case ConditionOperator.GreaterEqual:
                case ConditionOperator.LessThan:
                case ConditionOperator.LessEqual:
                    if (Operand(0) == null)
                    {
                        return false;
                    }

                    break;
                case ConditionOperator.Between:
                    if (Operand(0) == null || Operand(1) == null)
                    {
                        return false;
                    }

                    break;
            }

            switch (condition.Operator)
            {
                case ConditionOperator.Null:
                    return actual == null;
                case ConditionOperator.NotNull:
                    return actual != null;
                case ConditionOperator.Equal:
                    return EntityValues.ConditionEquals(actual, Operand(0));
                case ConditionOperator.NotEqual:
                    return !EntityValues.ConditionEquals(actual, Operand(0));
                case ConditionOperator.In:
                    return values.Any(v => EntityValues.ConditionEquals(actual, v));
                case ConditionOperator.NotIn:
                    return !values.Any(v => EntityValues.ConditionEquals(actual, v));
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
                    return DatePart(actual) == _today;
                case ConditionOperator.Yesterday:
                    return DatePart(actual) == _today.AddDays(-1);
                case ConditionOperator.Tomorrow:
                    return DatePart(actual) == _today.AddDays(1);
                case ConditionOperator.ThisMonth:
                    return DatePart(actual) is DateTime tm && tm.Year == _today.Year && tm.Month == _today.Month;
                case ConditionOperator.ThisYear:
                    return DatePart(actual) is DateTime ty && ty.Year == _today.Year;
                case ConditionOperator.LastXDays:
                    return InDayRange(actual, -ToInt(Operand(0)), 0);
                case ConditionOperator.NextXDays:
                    return InDayRange(actual, 0, ToInt(Operand(0)));
                case ConditionOperator.OlderThanXDays:
                    return DatePart(actual) is DateTime ox && ox < _today.AddDays(-ToInt(Operand(0)));
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
            // EntityName targets a linked entity by alias: read its column from the joined row, selected or not (an
            // unmatched outer join has no linked record, so the column reads as null).
            return string.IsNullOrEmpty(condition.EntityName)
                ? EntityValues.GetValue(entity, condition.AttributeName)
                : RowValue(entity, condition.EntityName + "." + condition.AttributeName);
        }

        // A column comparison (CompareColumns / FetchXML valueof): the single value names the other column, on the
        // same table as the condition (or "{alias}.{column}" for a linked one). Dataverse only compares columns with
        // eq/ne/gt/ge/lt/le, and a NULL on either side never satisfies the comparison (SQL three-valued logic).
        private static bool EvaluateColumnComparison(Entity entity, ConditionExpression condition, object? actual)
        {
            if (condition.Values.Count != 1)
            {
                throw new NotSupportedException(
                    $"A column comparison on '{condition.AttributeName}' must name exactly one other column.");
            }

            string other = EntityValues.AsString(condition.Values[0]);
            string key = other.IndexOf('.') < 0 && !string.IsNullOrEmpty(condition.EntityName) ? condition.EntityName + "." + other : other;
            object? operand = RowValue(entity, key);

            switch (condition.Operator)
            {
                case ConditionOperator.Equal:
                case ConditionOperator.NotEqual:
                case ConditionOperator.GreaterThan:
                case ConditionOperator.GreaterEqual:
                case ConditionOperator.LessThan:
                case ConditionOperator.LessEqual:
                    break;
                default:
                    throw new NotSupportedException(
                        $"InMemoryOrganizationService does not support the '{condition.Operator}' operator in a column comparison (only eq, ne, gt, ge, lt, le).");
            }

            if (actual == null || operand == null)
            {
                return false;
            }

            switch (condition.Operator)
            {
                case ConditionOperator.Equal: return EntityValues.AreEqual(actual, operand);
                case ConditionOperator.NotEqual: return !EntityValues.AreEqual(actual, operand);
                case ConditionOperator.GreaterThan: return EntityValues.Compare(actual, operand) > 0;
                case ConditionOperator.GreaterEqual: return EntityValues.Compare(actual, operand) >= 0;
                case ConditionOperator.LessThan: return EntityValues.Compare(actual, operand) < 0;
                default: return EntityValues.Compare(actual, operand) <= 0; // LessEqual
            }
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

        private bool InDayRange(object? value, int fromDays, int toDays)
        {
            DateTime? date = DatePart(value);
            if (date == null)
            {
                return false;
            }

            return date.Value >= _today.AddDays(fromDays) && date.Value <= _today.AddDays(toDays);
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
                        return comparable.CompareTo(Convert.ChangeType(y, x.GetType(), System.Globalization.CultureInfo.InvariantCulture));
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
