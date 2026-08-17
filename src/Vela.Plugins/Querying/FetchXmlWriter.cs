using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Querying;

/// <summary>
/// FetchXML-only options that a <see cref="QueryExpression"/> can't represent. They are emitted by
/// <see cref="FetchXmlWriter"/> only — they have no effect when the query runs as a <see cref="QueryExpression"/>.
/// </summary>
public sealed class FetchXmlOptions
{
    /// <summary>Emit <c>latematerialize='true'</c> — a FetchXML-only performance hint.</summary>
    public bool LateMaterialize { get; set; }

    /// <summary>Emit a custom <c>aggregatelimit</c> (FetchXML-only) below the 50,000 aggregate limit.</summary>
    public int? AggregateLimit { get; set; }
}

/// <summary>
/// Serializes a <see cref="QueryExpression"/> to a FetchXML string, offline and with no connection — the inverse of
/// the FetchXML parser the in-memory harness uses. Pairs with <c>Query.For&lt;T&gt;().ToFetchXml()</c> so a query can
/// be authored with compile-time type safety and emitted as FetchXML for the messages/tools that require the string.
/// </summary>
public static class FetchXmlWriter
{
    /// <summary>Serializes <paramref name="query"/> to a compact FetchXML string.</summary>
    public static string Write(QueryExpression query, FetchXmlOptions? options = null)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        ColumnSet columns = query.ColumnSet ?? new ColumnSet();
        bool aggregate = columns.AttributeExpressions != null && columns.AttributeExpressions.Count > 0;

        XElement fetch = new XElement("fetch");
        if (aggregate)
        {
            fetch.SetAttributeValue("aggregate", "true");
        }

        if (query.Distinct)
        {
            fetch.SetAttributeValue("distinct", "true");
        }

        if (query.TopCount.HasValue)
        {
            fetch.SetAttributeValue("top", Int(query.TopCount.Value));
        }

        if (query.PageInfo != null)
        {
            if (query.PageInfo.Count > 0)
            {
                fetch.SetAttributeValue("count", Int(query.PageInfo.Count));
            }

            if (query.PageInfo.PageNumber > 0)
            {
                fetch.SetAttributeValue("page", Int(query.PageInfo.PageNumber));
            }

            if (!string.IsNullOrEmpty(query.PageInfo.PagingCookie))
            {
                fetch.SetAttributeValue("paging-cookie", query.PageInfo.PagingCookie);
            }

            if (query.PageInfo.ReturnTotalRecordCount)
            {
                fetch.SetAttributeValue("returntotalrecordcount", "true");
            }
        }

        if (options?.LateMaterialize == true)
        {
            fetch.SetAttributeValue("latematerialize", "true");
        }

        if (options?.AggregateLimit is int aggregateLimit)
        {
            fetch.SetAttributeValue("aggregatelimit", Int(aggregateLimit));
        }

        XElement entity = new XElement("entity", new XAttribute("name", query.EntityName ?? string.Empty));
        WriteColumns(entity, columns);

        XElement? criteria = WriteFilter(query.Criteria);
        if (criteria != null)
        {
            entity.Add(criteria);
        }

        foreach (OrderExpression order in query.Orders ?? Enumerable.Empty<OrderExpression>())
        {
            entity.Add(WriteOrder(order));
        }

        foreach (LinkEntity link in query.LinkEntities ?? Enumerable.Empty<LinkEntity>())
        {
            entity.Add(WriteLink(link));
        }

        fetch.Add(entity);
        return fetch.ToString(SaveOptions.DisableFormatting);
    }

    private static void WriteColumns(XElement parent, ColumnSet columns)
    {
        if (columns == null)
        {
            return;
        }

        if (columns.AllColumns)
        {
            parent.Add(new XElement("all-attributes"));
        }
        else
        {
            foreach (string column in columns.Columns ?? Enumerable.Empty<string>())
            {
                parent.Add(new XElement("attribute", new XAttribute("name", column)));
            }
        }

        foreach (XrmAttributeExpression expression in columns.AttributeExpressions ?? Enumerable.Empty<XrmAttributeExpression>())
        {
            XElement attribute = new XElement("attribute", new XAttribute("name", expression.AttributeName ?? string.Empty));
            if (!string.IsNullOrEmpty(expression.Alias))
            {
                attribute.SetAttributeValue("alias", expression.Alias);
            }

            if (expression.HasGroupBy)
            {
                attribute.SetAttributeValue("groupby", "true");
                if (expression.DateTimeGrouping != XrmDateTimeGrouping.None)
                {
                    attribute.SetAttributeValue("dategrouping", DateGrouping(expression.DateTimeGrouping));
                }
            }
            else if (expression.AggregateType != XrmAggregateType.None)
            {
                attribute.SetAttributeValue("aggregate", AggregateName(expression.AggregateType));
            }

            parent.Add(attribute);
        }
    }

    private static XElement? WriteFilter(FilterExpression? filter)
    {
        if (filter == null)
        {
            return null;
        }

        bool hasConditions = filter.Conditions != null && filter.Conditions.Count > 0;
        bool hasFilters = filter.Filters != null && filter.Filters.Count > 0;
        bool hasAnyAll = filter.AnyAllFilterLinkEntity != null;
        if (!hasConditions && !hasFilters && !hasAnyAll)
        {
            return null;
        }

        XElement element = new XElement("filter", new XAttribute("type", filter.FilterOperator == LogicalOperator.Or ? "or" : "and"));
        foreach (ConditionExpression condition in filter.Conditions ?? Enumerable.Empty<ConditionExpression>())
        {
            element.Add(WriteCondition(condition));
        }

        foreach (FilterExpression nested in filter.Filters ?? Enumerable.Empty<FilterExpression>())
        {
            XElement? child = WriteFilter(nested);
            if (child != null)
            {
                element.Add(child);
            }
        }

        if (filter.AnyAllFilterLinkEntity != null)
        {
            element.Add(WriteLink(filter.AnyAllFilterLinkEntity));
        }

        return element;
    }

    private static XElement WriteCondition(ConditionExpression condition)
    {
        XElement element = new XElement(
            "condition",
            new XAttribute("attribute", condition.AttributeName ?? string.Empty),
            new XAttribute("operator", OperatorName(condition.Operator)));

        if (!string.IsNullOrEmpty(condition.EntityName))
        {
            element.SetAttributeValue("entityname", condition.EntityName);
        }

        IReadOnlyList<object> values = condition.Values ?? (IReadOnlyList<object>)Array.Empty<object>();

        // Cross-column comparison (same row): valueof carries the other column's name, not a literal.
        if (condition.CompareColumns && values.Count > 0)
        {
            element.SetAttributeValue("valueof", Convert.ToString(values[0], CultureInfo.InvariantCulture));
            return element;
        }

        if (values.Count == 1)
        {
            element.SetAttributeValue("value", FormatValue(values[0]));
        }
        else if (values.Count > 1)
        {
            foreach (object value in values)
            {
                element.Add(new XElement("value", FormatValue(value)));
            }
        }

        return element;
    }

    private static XElement WriteOrder(OrderExpression order)
    {
        XElement element = new XElement("order", new XAttribute("attribute", order.AttributeName ?? string.Empty));
        if (order.OrderType == OrderType.Descending)
        {
            element.SetAttributeValue("descending", "true");
        }

        return element;
    }

    // The FetchXML from/to attributes are the INVERSE of QueryExpression: FetchXML `from` is the attribute on the
    // linked (child) entity = LinkToAttributeName; `to` is the attribute on the parent = LinkFromAttributeName.
    private static XElement WriteLink(LinkEntity link)
    {
        XElement element = new XElement(
            "link-entity",
            new XAttribute("name", link.LinkToEntityName ?? string.Empty),
            new XAttribute("from", link.LinkToAttributeName ?? string.Empty),
            new XAttribute("to", link.LinkFromAttributeName ?? string.Empty));

        string? linkType = LinkType(link.JoinOperator);
        if (linkType != null)
        {
            element.SetAttributeValue("link-type", linkType);
        }

        if (!string.IsNullOrEmpty(link.EntityAlias))
        {
            element.SetAttributeValue("alias", link.EntityAlias);
        }

        WriteColumns(element, link.Columns);

        XElement? criteria = WriteFilter(link.LinkCriteria);
        if (criteria != null)
        {
            element.Add(criteria);
        }

        foreach (LinkEntity child in link.LinkEntities ?? Enumerable.Empty<LinkEntity>())
        {
            element.Add(WriteLink(child));
        }

        return element;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string? LinkType(JoinOperator op) => op switch
    {
        JoinOperator.Inner => null, // FetchXML default
        JoinOperator.LeftOuter => "outer",
        JoinOperator.Natural => "natural",
        JoinOperator.Exists => "exists",
        JoinOperator.In => "in",
        JoinOperator.MatchFirstRowUsingCrossApply => "matchfirstrowusingcrossapply",
        JoinOperator.Any => "any",
        JoinOperator.NotAny => "not any",
        JoinOperator.All => "all",
        JoinOperator.NotAll => "not all",
        _ => null
    };

    private static string AggregateName(XrmAggregateType type) => type switch
    {
        XrmAggregateType.Count => "count",
        XrmAggregateType.CountColumn => "countcolumn",
        XrmAggregateType.Sum => "sum",
        XrmAggregateType.Avg => "avg",
        XrmAggregateType.Min => "min",
        XrmAggregateType.Max => "max",
        _ => "count"
    };

    private static string DateGrouping(XrmDateTimeGrouping grouping) => grouping switch
    {
        XrmDateTimeGrouping.Day => "day",
        XrmDateTimeGrouping.Week => "week",
        XrmDateTimeGrouping.Month => "month",
        XrmDateTimeGrouping.Quarter => "quarter",
        XrmDateTimeGrouping.Year => "year",
        XrmDateTimeGrouping.FiscalPeriod => "fiscal-period",
        XrmDateTimeGrouping.FiscalYear => "fiscal-year",
        _ => "day"
    };

    // Irregular short operators are mapped explicitly; the rest fall back to kebab-case, which matches most
    // date/relative operators (on-or-after, last-x-days, this-month, in-fiscal-year…). A few irregular ones
    // (eq-userid family, olderthan-x-*) would need explicit cases if used.
    private static string OperatorName(ConditionOperator op) => op switch
    {
        ConditionOperator.Equal => "eq",
        ConditionOperator.NotEqual => "ne",
        ConditionOperator.GreaterThan => "gt",
        ConditionOperator.GreaterEqual => "ge",
        ConditionOperator.LessThan => "lt",
        ConditionOperator.LessEqual => "le",
        ConditionOperator.Like => "like",
        ConditionOperator.NotLike => "not-like",
        ConditionOperator.In => "in",
        ConditionOperator.NotIn => "not-in",
        ConditionOperator.Between => "between",
        ConditionOperator.NotBetween => "not-between",
        ConditionOperator.Null => "null",
        ConditionOperator.NotNull => "not-null",
        ConditionOperator.BeginsWith => "begins-with",
        ConditionOperator.DoesNotBeginWith => "not-begin-with",
        ConditionOperator.EndsWith => "ends-with",
        ConditionOperator.DoesNotEndWith => "not-end-with",
        _ => KebabCase(op.ToString())
    };

    private static string KebabCase(string pascal)
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
