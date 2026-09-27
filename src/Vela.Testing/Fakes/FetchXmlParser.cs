using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    /// <summary>
    /// Converts a FetchXML string into a <see cref="QueryExpression"/> so the in-memory engine can execute it
    /// through the same path as QueryExpression. Supports a common subset: attributes/all-attributes, filters
    /// (and/or, nested), the usual operators, link-entity (inner/outer, alias, columns, filters, nested),
    /// order, top, distinct and paging (page/count). Unsupported operators throw a clear message.
    /// </summary>
    internal static class FetchXmlParser
    {
        public static QueryExpression Parse(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml))
            {
                throw new ArgumentException("FetchXML is empty.", nameof(fetchXml));
            }

            XElement fetch = XDocument.Parse(fetchXml).Root
                ?? throw new NotSupportedException("Invalid FetchXML: missing <fetch> root.");
            XElement entity = fetch.Element("entity")
                ?? throw new NotSupportedException("FetchXML must contain an <entity> element.");

            QueryExpression query = new QueryExpression(Attribute(entity, "name"))
            {
                ColumnSet = ParseColumns(entity),
                Distinct = string.Equals(Attribute(fetch, "distinct"), "true", StringComparison.OrdinalIgnoreCase)
            };

            string? top = Attribute(fetch, "top");
            if (!string.IsNullOrEmpty(top))
            {
                query.TopCount = int.Parse(top, CultureInfo.InvariantCulture);
            }

            string? count = Attribute(fetch, "count");
            if (!string.IsNullOrEmpty(count))
            {
                string? page = Attribute(fetch, "page");
                query.PageInfo = new PagingInfo
                {
                    Count = int.Parse(count, CultureInfo.InvariantCulture),
                    PageNumber = string.IsNullOrEmpty(page) ? 1 : int.Parse(page, CultureInfo.InvariantCulture)
                };
            }

            foreach (XElement filter in entity.Elements("filter"))
            {
                query.Criteria.AddFilter(ParseFilter(filter));
            }

            foreach (XElement order in entity.Elements("order"))
            {
                query.AddOrder(Attribute(order, "attribute"), IsDescending(order) ? OrderType.Descending : OrderType.Ascending);
            }

            foreach (XElement link in entity.Elements("link-entity"))
            {
                query.LinkEntities.Add(ParseLink(link));
            }

            return query;
        }

        /// <summary>True when the FetchXML declares <c>aggregate="true"</c>.</summary>
        public static bool IsAggregate(string fetchXml)
        {
            XElement? fetch = XDocument.Parse(fetchXml).Root;
            return fetch != null && string.Equals(fetch.Attribute("aggregate")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parses an aggregate FetchXML into an <see cref="AggregateQuery"/>.</summary>
        public static AggregateQuery ParseAggregate(string fetchXml)
        {
            XElement fetch = XDocument.Parse(fetchXml).Root
                ?? throw new NotSupportedException("Invalid FetchXML: missing <fetch> root.");
            XElement entity = fetch.Element("entity")
                ?? throw new NotSupportedException("FetchXML must contain an <entity> element.");

            AggregateQuery query = new AggregateQuery { EntityName = Attribute(entity, "name") };

            string? top = Attribute(fetch, "top");
            if (!string.IsNullOrEmpty(top))
            {
                query.Top = int.Parse(top, CultureInfo.InvariantCulture);
            }

            foreach (XElement attribute in entity.Elements("attribute"))
            {
                string name = Attribute(attribute, "name");
                string alias = Attribute(attribute, "alias");
                string? aggregate = attribute.Attribute("aggregate")?.Value;
                bool groupBy = string.Equals(attribute.Attribute("groupby")?.Value, "true", StringComparison.OrdinalIgnoreCase);

                if (groupBy)
                {
                    query.Groups.Add(new AggregateColumn { Attribute = name, Alias = alias });
                }
                else if (!string.IsNullOrEmpty(aggregate))
                {
                    query.Aggregates.Add(new AggregateColumn { Attribute = name, Alias = alias, Function = aggregate!.ToLowerInvariant() });
                }
            }

            foreach (XElement filter in entity.Elements("filter"))
            {
                query.Criteria.AddFilter(ParseFilter(filter));
            }

            foreach (XElement order in entity.Elements("order"))
            {
                string alias = Attribute(order, "alias");
                string key = string.IsNullOrEmpty(alias) ? Attribute(order, "attribute") : alias;
                query.Orders.Add(new AggregateColumn { Alias = key, Descending = IsDescending(order) });
            }

            return query;
        }

        private static ColumnSet ParseColumns(XElement element)
        {
            if (element.Element("all-attributes") != null)
            {
                return new ColumnSet(true);
            }

            string[] names = element.Elements("attribute").Select(a => Attribute(a, "name")).ToArray();
            return names.Length == 0 ? new ColumnSet(false) : new ColumnSet(names);
        }

        private static LinkEntity ParseLink(XElement element)
        {
            JoinOperator join = string.Equals(Attribute(element, "link-type"), "outer", StringComparison.OrdinalIgnoreCase)
                ? JoinOperator.LeftOuter
                : JoinOperator.Inner;

            // FetchXML: name = linked entity, from = attribute on the linked entity, to = attribute on the parent.
            LinkEntity link = new LinkEntity(
                null,
                Attribute(element, "name"),
                Attribute(element, "to"),
                Attribute(element, "from"),
                join)
            {
                Columns = ParseColumns(element)
            };

            string? alias = Attribute(element, "alias");
            if (!string.IsNullOrEmpty(alias))
            {
                link.EntityAlias = alias;
            }

            foreach (XElement filter in element.Elements("filter"))
            {
                link.LinkCriteria.AddFilter(ParseFilter(filter));
            }

            foreach (XElement child in element.Elements("link-entity"))
            {
                link.LinkEntities.Add(ParseLink(child));
            }

            return link;
        }

        private static FilterExpression ParseFilter(XElement element)
        {
            LogicalOperator op = string.Equals(Attribute(element, "type"), "or", StringComparison.OrdinalIgnoreCase)
                ? LogicalOperator.Or
                : LogicalOperator.And;

            FilterExpression filter = new FilterExpression(op);
            foreach (XElement condition in element.Elements("condition"))
            {
                filter.AddCondition(ParseCondition(condition));
            }

            foreach (XElement nested in element.Elements("filter"))
            {
                filter.AddFilter(ParseFilter(nested));
            }

            return filter;
        }

        private static ConditionExpression ParseCondition(XElement element)
        {
            string attribute = Attribute(element, "attribute");
            ConditionOperator op = MapOperator(Attribute(element, "operator"));

            if (op == ConditionOperator.Null || op == ConditionOperator.NotNull)
            {
                return new ConditionExpression(attribute, op);
            }

            List<object> values = new List<object>();
            string? inline = element.Attribute("value")?.Value;
            if (inline != null)
            {
                values.Add(inline);
            }

            foreach (XElement value in element.Elements("value"))
            {
                values.Add(value.Value);
            }

            return new ConditionExpression(attribute, op, values.ToArray());
        }

        private static ConditionOperator MapOperator(string? fetchOperator)
        {
            switch ((fetchOperator ?? string.Empty).ToLowerInvariant())
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

        private static bool IsDescending(XElement order)
        {
            return string.Equals(Attribute(order, "descending"), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Attribute(XElement element, string name)
        {
            return element.Attribute(name)?.Value ?? string.Empty;
        }
    }

    /// <summary>A parsed aggregate FetchXML query (group-by + aggregate columns).</summary>
    internal sealed class AggregateQuery
    {
        public string EntityName { get; set; } = string.Empty;

        public FilterExpression Criteria { get; } = new FilterExpression(LogicalOperator.And);

        public List<AggregateColumn> Groups { get; } = new List<AggregateColumn>();

        public List<AggregateColumn> Aggregates { get; } = new List<AggregateColumn>();

        public List<AggregateColumn> Orders { get; } = new List<AggregateColumn>();

        public int? Top { get; set; }
    }

    /// <summary>A group-by, aggregate or order column in an <see cref="AggregateQuery"/>.</summary>
    internal sealed class AggregateColumn
    {
        public string Attribute { get; set; } = string.Empty;

        public string Alias { get; set; } = string.Empty;

        public string Function { get; set; } = string.Empty;

        public bool Descending { get; set; }
    }
}
