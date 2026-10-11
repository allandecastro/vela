using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// Converts a FetchXML string into the equivalent <see cref="QueryExpression"/> so the in-memory engine runs it
    /// through exactly the same path as a QueryExpression (aggregates included: they become
    /// <see cref="ColumnSet.AttributeExpressions"/>). The parser is strict: every element, attribute and value it
    /// does not model throws a <see cref="NotSupportedException"/> naming it, so an unsupported construct can never
    /// be silently ignored and return the wrong rows.
    /// </summary>
    internal static class FetchXmlParser
    {
        // fetch: accepted attributes. no-lock/options/paging-cookie/latematerialize/useraworderby/mapping/version/
        // output-format are hints or legacy envelope attributes with no effect on the rows an in-memory store returns.
        private static readonly string[] FetchAttributes =
        {
            "aggregate", "distinct", "top", "count", "page", "paging-cookie", "returntotalrecordcount", "no-lock",
            "latematerialize", "aggregatelimit", "options", "useraworderby", "mapping", "version", "output-format"
        };

        private static readonly string[] EntityChildren = { "attribute", "all-attributes", "filter", "order", "link-entity" };

        // visible/intersect are presentation hints (Advanced Find / N:N intersect marker): no effect on the rows.
        private static readonly string[] LinkAttributes = { "name", "from", "to", "link-type", "alias", "visible", "intersect" };

        private static readonly string[] AttributeAttributes = { "name", "alias", "aggregate", "groupby", "dategrouping", "usertimezone", "distinct" };

        // uiname/uitype/uihidden are display metadata Advanced Find writes on lookup conditions.
        private static readonly string[] ConditionAttributes = { "attribute", "operator", "value", "entityname", "valueof", "uiname", "uitype", "uihidden" };

        public static FetchQuery Parse(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml))
            {
                throw new ArgumentException("FetchXML is empty.", nameof(fetchXml));
            }

            XElement fetch = XDocument.Parse(fetchXml).Root!;
            if (fetch.Name.LocalName != "fetch")
            {
                throw new NotSupportedException($"Invalid FetchXML: the root element must be <fetch>, not <{fetch.Name.LocalName}>.");
            }

            ValidateAttributes(fetch, FetchAttributes);
            ValidateChildren(fetch, "entity");
            List<XElement> entities = fetch.Elements().ToList();
            if (entities.Count != 1)
            {
                throw new NotSupportedException("FetchXML must contain exactly one <entity> element.");
            }

            XElement entity = entities[0];
            ValidateAttributes(entity, "name");
            ValidateChildren(entity, EntityChildren);

            FetchQuery result = new FetchQuery { Aggregate = Bool(fetch, "aggregate") };
            QueryExpression query = new QueryExpression(Required(entity, "name")) { Distinct = Bool(fetch, "distinct") };
            result.Query = query;

            if (Int(fetch, "top") is int top)
            {
                query.TopCount = top;
            }

            // The engine pages by page number (the writer always emits it with the paging-cookie), so the cookie is
            // accepted but not needed. A page with no count uses the platform's default page size (5,000 rows).
            int? count = Int(fetch, "count");
            int? page = Int(fetch, "page");
            bool totalCount = Bool(fetch, "returntotalrecordcount");
            if (totalCount && query.TopCount.HasValue)
            {
                // Verified live: 0x80040203.
                throw PlatformFault.Create(PlatformFault.InvalidArgument,
                    "The top attribute can't be specified with paging attribute returntotalrecordcount");
            }
            if (count.HasValue || page.HasValue || totalCount)
            {
                query.PageInfo = new PagingInfo
                {
                    Count = count ?? InMemoryQueryEngine.MaxPageSize,
                    PageNumber = page ?? 1,
                    ReturnTotalRecordCount = totalCount
                };
            }

            result.AggregateLimit = Int(fetch, "aggregatelimit");
            if (result.AggregateLimit.HasValue && !result.Aggregate)
            {
                throw new NotSupportedException("FetchXML aggregatelimit requires aggregate='true'.");
            }

            ParseColumns(entity, query.ColumnSet, result);
            foreach (XElement filter in entity.Elements("filter"))
            {
                query.Criteria.AddFilter(ParseFilter(filter));
            }

            foreach (XElement order in entity.Elements("order"))
            {
                query.Orders.Add(ParseOrder(order, result.Aggregate, linked: false));
            }

            foreach (XElement link in entity.Elements("link-entity"))
            {
                query.LinkEntities.Add(ParseLink(link, result, insideFilter: false));
            }

            if (result.Aggregate && !HasAggregateColumn(query))
            {
                throw new NotSupportedException("FetchXML aggregate='true' requires at least one attribute with aggregate or groupby.");
            }

            return result;
        }

        private static bool HasAggregateColumn(QueryExpression query)
        {
            return query.ColumnSet.AttributeExpressions.Count > 0 || query.LinkEntities.Any(HasAggregateColumn);
        }

        private static bool HasAggregateColumn(LinkEntity link)
        {
            return link.Columns.AttributeExpressions.Count > 0 || link.LinkEntities.Any(HasAggregateColumn);
        }

        // Fills a ColumnSet from <attribute>/<all-attributes>. In an aggregate fetch every attribute must be a
        // group-by or an aggregate (an XrmAttributeExpression); in a plain fetch an aliased attribute becomes a
        // column alias (an XrmAttributeExpression with no aggregate).
        private static void ParseColumns(XElement element, ColumnSet columns, FetchQuery fetch)
        {
            if (element.Element("all-attributes") is XElement all)
            {
                ValidateAttributes(all);
                ValidateChildren(all);
                if (fetch.Aggregate)
                {
                    throw new NotSupportedException("FetchXML <all-attributes> is not allowed in an aggregate query.");
                }

                columns.AllColumns = true;
            }

            foreach (XElement attribute in element.Elements("attribute"))
            {
                if (attribute.Attribute("rowaggregate") != null)
                {
                    throw new NotSupportedException("FetchXML attribute 'rowaggregate' on <attribute> (hierarchical row aggregate) is not supported by the in-memory harness.");
                }

                ValidateAttributes(attribute, AttributeAttributes);
                ValidateChildren(attribute);
                string name = Required(attribute, "name");
                string? alias = attribute.Attribute("alias")?.Value;

                if (!fetch.Aggregate)
                {
                    foreach (string aggregateOnly in new[] { "aggregate", "groupby", "dategrouping", "usertimezone", "distinct" })
                    {
                        if (attribute.Attribute(aggregateOnly) != null)
                        {
                            throw new NotSupportedException($"FetchXML attribute '{aggregateOnly}' on <attribute> requires aggregate='true' on <fetch>.");
                        }
                    }

                    if (string.IsNullOrEmpty(alias))
                    {
                        columns.AddColumn(name);
                    }
                    else
                    {
                        columns.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = name, Alias = alias, AggregateType = XrmAggregateType.None });
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(alias))
                {
                    throw new NotSupportedException($"FetchXML aggregate attribute '{name}' requires an alias.");
                }

                bool groupBy = Bool(attribute, "groupby");
                string? function = attribute.Attribute("aggregate")?.Value;
                XrmAttributeExpression expression = new XrmAttributeExpression { AttributeName = name, Alias = alias };
                if (groupBy)
                {
                    if (function != null || attribute.Attribute("distinct") != null)
                    {
                        throw new NotSupportedException($"FetchXML attribute '{name}' cannot be both groupby and aggregate.");
                    }

                    expression.HasGroupBy = true;
                    expression.DateTimeGrouping = MapDateGrouping(attribute.Attribute("dategrouping")?.Value);
                }
                else if (function != null)
                {
                    if (attribute.Attribute("dategrouping") != null || attribute.Attribute("usertimezone") != null)
                    {
                        throw new NotSupportedException($"FetchXML dategrouping/usertimezone on '{name}' require groupby='true'.");
                    }

                    expression.AggregateType = MapAggregate(function);
                    if (Bool(attribute, "distinct"))
                    {
                        if (expression.AggregateType != XrmAggregateType.CountColumn)
                        {
                            throw new NotSupportedException($"FetchXML distinct='true' is only supported with aggregate='countcolumn', not '{function}'.");
                        }

                        fetch.DistinctAggregateAliases.Add(alias!);
                    }
                }
                else
                {
                    throw new NotSupportedException($"FetchXML aggregate query attribute '{name}' must have aggregate or groupby='true'.");
                }

                columns.AttributeExpressions.Add(expression);
            }
        }

        private static LinkEntity ParseLink(XElement element, FetchQuery fetch, bool insideFilter)
        {
            ValidateAttributes(element, LinkAttributes);
            ValidateChildren(element, EntityChildren);
            string? linkType = element.Attribute("link-type")?.Value;
            JoinOperator join = MapLinkType(linkType);
            bool anyAll = join is JoinOperator.Any or JoinOperator.NotAny or JoinOperator.All or JoinOperator.NotAll;
            if (insideFilter && !anyAll)
            {
                throw new NotSupportedException($"FetchXML <link-entity> inside <filter> must use link-type any, not any, all or not all (got '{linkType ?? "inner"}').");
            }

            // FetchXML: name = linked entity, from = attribute on the linked entity, to = attribute on the parent.
            LinkEntity link = new LinkEntity(null, Required(element, "name"), Required(element, "to"), Required(element, "from"), join);
            string? alias = element.Attribute("alias")?.Value;
            if (!string.IsNullOrEmpty(alias))
            {
                link.EntityAlias = alias;
            }

            ParseColumns(element, link.Columns, fetch);
            foreach (XElement filter in element.Elements("filter"))
            {
                link.LinkCriteria.AddFilter(ParseFilter(filter));
            }

            foreach (XElement order in element.Elements("order"))
            {
                link.Orders.Add(ParseOrder(order, fetch.Aggregate, linked: true));
            }

            foreach (XElement child in element.Elements("link-entity"))
            {
                link.LinkEntities.Add(ParseLink(child, fetch, insideFilter: false));
            }

            return link;
        }

        private static FilterExpression ParseFilter(XElement element)
        {
            ValidateAttributes(element, "type", "hint", "isquickfindfields");
            ValidateChildren(element, "condition", "filter", "link-entity");
            string type = element.Attribute("type")?.Value ?? "and";
            LogicalOperator op;
            switch (type)
            {
                case "and": op = LogicalOperator.And; break;
                case "or": op = LogicalOperator.Or; break;
                default: throw new NotSupportedException($"FetchXML filter type '{type}' is not supported (use 'and' or 'or').");
            }

            // hint / isquickfindfields are SQL / quick-find hints with no effect on the rows: accepted, not read.
            FilterExpression filter = new FilterExpression(op);

            foreach (XElement child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "condition":
                        filter.AddCondition(ParseCondition(child));
                        break;
                    case "filter":
                        filter.AddFilter(ParseFilter(child));
                        break;
                    default:
                        // A FilterExpression holds a single AnyAllFilterLinkEntity, so each related-record filter becomes
                        // a single-predicate child filter, which the parent's and/or combines identically.
                        filter.AddFilter(new FilterExpression(LogicalOperator.And)
                        {
                            AnyAllFilterLinkEntity = ParseLink(child, new FetchQuery(), insideFilter: true)
                        });
                        break;
                }
            }

            return filter;
        }

        private static ConditionExpression ParseCondition(XElement element)
        {
            ValidateAttributes(element, ConditionAttributes);
            ValidateChildren(element, "value");
            string attribute = Required(element, "attribute");
            ConditionOperator op = MapOperator(Required(element, "operator"));
            string? entityName = element.Attribute("entityname")?.Value;
            string? valueOf = element.Attribute("valueof")?.Value;

            ConditionExpression condition;
            if (valueOf != null)
            {
                if (element.Attribute("value") != null || element.Elements("value").Any())
                {
                    throw new NotSupportedException($"FetchXML condition on '{attribute}' cannot combine valueof with a value.");
                }

                condition = new ConditionExpression(attribute, op, true, valueOf);
            }
            else
            {
                // Operators that take no value (null, not-null, today, ...) simply ignore any value supplied.
                List<object> values = new List<object>();
                string? inline = element.Attribute("value")?.Value;
                if (inline != null)
                {
                    values.Add(inline);
                }

                foreach (XElement value in element.Elements("value"))
                {
                    ValidateAttributes(value, "uiname", "uitype");
                    ValidateChildren(value);
                    values.Add(value.Value);
                }

                condition = new ConditionExpression(attribute, op, values.ToArray());
            }

            // entityname targets a link-entity by alias (null = the condition's own table).
            condition.EntityName = entityName;
            return condition;
        }

        private static OrderExpression ParseOrder(XElement element, bool aggregate, bool linked)
        {
            ValidateAttributes(element, "attribute", "alias", "descending", "entityname");
            ValidateChildren(element);
            OrderExpression order = new OrderExpression
            {
                OrderType = Bool(element, "descending") ? OrderType.Descending : OrderType.Ascending
            };

            string? alias = element.Attribute("alias")?.Value;
            string? attribute = element.Attribute("attribute")?.Value;
            string? entityName = element.Attribute("entityname")?.Value;
            if (aggregate)
            {
                // An aggregate result is ordered by a group-by/aggregate alias (attribute= names it on older fetches).
                if (linked || entityName != null)
                {
                    throw new NotSupportedException("FetchXML aggregate queries order by an alias on the root <entity> only.");
                }

                order.Alias = alias ?? attribute;
                return order;
            }

            if (alias != null)
            {
                throw new NotSupportedException($"FetchXML <order alias='{alias}'> is only supported in an aggregate query.");
            }

            if (linked && entityName != null)
            {
                throw new NotSupportedException("FetchXML <order entityname> is only supported on the root <entity>.");
            }

            order.AttributeName = attribute ?? throw new NotSupportedException("FetchXML <order> requires an attribute.");
            order.EntityName = entityName;
            return order;
        }

        private static JoinOperator MapLinkType(string? linkType)
        {
            switch (linkType)
            {
                case null:
                case "inner": return JoinOperator.Inner;
                case "outer": return JoinOperator.LeftOuter;
                case "any": return JoinOperator.Any;
                case "not any": return JoinOperator.NotAny;
                case "all": return JoinOperator.All;
                case "not all": return JoinOperator.NotAll;
                case "exists": return JoinOperator.Exists;
                case "in": return JoinOperator.In;
                case "matchfirstrowusingcrossapply": return JoinOperator.MatchFirstRowUsingCrossApply;
                default:
                    throw new NotSupportedException($"FetchXML link-type '{linkType}' is not supported by the in-memory harness.");
            }
        }

        private static XrmAggregateType MapAggregate(string function)
        {
            switch (function)
            {
                case "count": return XrmAggregateType.Count;
                case "countcolumn": return XrmAggregateType.CountColumn;
                case "sum": return XrmAggregateType.Sum;
                case "avg": return XrmAggregateType.Avg;
                case "min": return XrmAggregateType.Min;
                case "max": return XrmAggregateType.Max;
                default:
                    throw new NotSupportedException($"FetchXML aggregate function '{function}' is not supported yet.");
            }
        }

        private static XrmDateTimeGrouping MapDateGrouping(string? grouping)
        {
            switch (grouping)
            {
                case null: return XrmDateTimeGrouping.None;
                case "day": return XrmDateTimeGrouping.Day;
                case "week": return XrmDateTimeGrouping.Week;
                case "month": return XrmDateTimeGrouping.Month;
                case "quarter": return XrmDateTimeGrouping.Quarter;
                case "year": return XrmDateTimeGrouping.Year;
                case "fiscal-period": return XrmDateTimeGrouping.FiscalPeriod;
                case "fiscal-year": return XrmDateTimeGrouping.FiscalYear;
                default:
                    throw new NotSupportedException($"FetchXML dategrouping '{grouping}' is not supported.");
            }
        }

        private static ConditionOperator MapOperator(string fetchOperator)
        {
            switch (fetchOperator.ToLowerInvariant())
            {
                case "eq": return ConditionOperator.Equal;
                case "ne":
                case "neq": return ConditionOperator.NotEqual;
                case "null": return ConditionOperator.Null;
                case "not-null": return ConditionOperator.NotNull;
                case "gt": return ConditionOperator.GreaterThan;
                case "ge": return ConditionOperator.GreaterEqual;
                case "lt": return ConditionOperator.LessThan;
                case "le": return ConditionOperator.LessEqual;
                case "in": return ConditionOperator.In;
                case "not-in": return ConditionOperator.NotIn;
                case "like": return ConditionOperator.Like;
                case "not-like": return ConditionOperator.NotLike;
                case "begins-with": return ConditionOperator.BeginsWith;
                case "not-begin-with": return ConditionOperator.DoesNotBeginWith;
                case "ends-with": return ConditionOperator.EndsWith;
                case "not-end-with": return ConditionOperator.DoesNotEndWith;
                case "between": return ConditionOperator.Between;
                case "not-between": return ConditionOperator.NotBetween;
                case "on": return ConditionOperator.On;
                case "on-or-before": return ConditionOperator.OnOrBefore;
                case "on-or-after": return ConditionOperator.OnOrAfter;
                case "today": return ConditionOperator.Today;
                case "yesterday": return ConditionOperator.Yesterday;
                case "tomorrow": return ConditionOperator.Tomorrow;
                case "this-month": return ConditionOperator.ThisMonth;
                case "this-year": return ConditionOperator.ThisYear;
                case "last-x-days": return ConditionOperator.LastXDays;
                case "next-x-days": return ConditionOperator.NextXDays;
                case "olderthan-x-days": return ConditionOperator.OlderThanXDays;
                case "eq-userid": return ConditionOperator.EqualUserId;
                case "ne-userid": return ConditionOperator.NotEqualUserId;
                default:
                    throw new NotSupportedException($"FetchXML operator '{fetchOperator}' is not supported yet.");
            }
        }

        // Rejects any attribute the parser does not model (namespace declarations aside), naming it.
        private static void ValidateAttributes(XElement element, params string[] allowed)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration && !allowed.Contains(attribute.Name.LocalName))
                {
                    throw new NotSupportedException(
                        $"FetchXML attribute '{attribute.Name.LocalName}' on <{element.Name.LocalName}> is not supported by the in-memory harness.");
                }
            }
        }

        // Rejects any child element the parser does not model, naming it.
        private static void ValidateChildren(XElement element, params string[] allowed)
        {
            foreach (XElement child in element.Elements())
            {
                if (!allowed.Contains(child.Name.LocalName))
                {
                    throw new NotSupportedException(
                        $"FetchXML element <{child.Name.LocalName}> inside <{element.Name.LocalName}> is not supported by the in-memory harness.");
                }
            }
        }

        private static string Required(XElement element, string name)
        {
            string? value = element.Attribute(name)?.Value;
            if (string.IsNullOrEmpty(value))
            {
                throw new NotSupportedException($"FetchXML <{element.Name.LocalName}> requires the '{name}' attribute.");
            }

            return value!;
        }

        private static bool Bool(XElement element, string name)
        {
            string? value = element.Attribute(name)?.Value;
            switch (value?.ToLowerInvariant())
            {
                case null:
                case "false":
                case "0":
                    return false;
                case "true":
                case "1":
                    return true;
                default:
                    throw new NotSupportedException($"FetchXML {name}='{value}' is not a boolean (use 'true' or 'false').");
            }
        }

        private static int? Int(XElement element, string name)
        {
            string? value = element.Attribute(name)?.Value;
            return value == null ? (int?)null : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A parsed FetchXML query: the equivalent <see cref="QueryExpression"/> plus the FetchXML-only aggregate options
    /// a QueryExpression cannot carry (distinct countcolumn, aggregatelimit).
    /// </summary>
    internal sealed class FetchQuery
    {
        public QueryExpression Query { get; set; } = new QueryExpression();

        public bool Aggregate { get; set; }

        /// <summary>Aliases of <c>countcolumn</c> aggregates declared <c>distinct='true'</c>.</summary>
        public HashSet<string> DistinctAggregateAliases { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The <c>aggregatelimit</c> (null = the platform default of 50,000 rows).</summary>
        public int? AggregateLimit { get; set; }
    }
}
