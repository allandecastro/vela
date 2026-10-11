using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Testing
{
    // Deep copies of the mutable SDK values a request parameter can hold, so the copy of a request the pipeline hands
    // to the steps shares nothing a step can edit in place with the caller's request. Immutable values (strings,
    // numbers, Guid, Money, OptionSetValue, FetchExpression's string, ...) are returned as they are.
    internal static class RequestValues
    {
        public static object? Clone(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case Entity entity:
                    return EntityValues.CloneTarget(entity);
                case EntityReferenceCollection references:
                    return new EntityReferenceCollection(references.Select(r => (EntityReference)EntityValues.CloneValue(r)!).ToList());
                case ColumnSet columns:
                    return Clone(columns);
                case QueryExpression query:
                    return Clone(query);
                case QueryByAttribute byAttribute:
                    return Clone(byAttribute);
                case FetchExpression fetch:
                    return new FetchExpression(fetch.Query);
                case string[] strings:
                    return (string[])strings.Clone();
                default:
                    // EntityReference (with its KeyAttributes), EntityCollection, OptionSetValueCollection, byte[], ...
                    return EntityValues.CloneValue(value);
            }
        }

        public static ColumnSet Clone(ColumnSet columns)
        {
            ColumnSet copy = columns.AllColumns ? new ColumnSet(true) : new ColumnSet(columns.Columns.ToArray());
            foreach (XrmAttributeExpression expression in columns.AttributeExpressions)
            {
                copy.AttributeExpressions.Add(new XrmAttributeExpression(expression.AttributeName, expression.AggregateType, expression.Alias)
                {
                    HasGroupBy = expression.HasGroupBy,
                    DateTimeGrouping = expression.DateTimeGrouping,
                });
            }

            return copy;
        }

        public static QueryExpression Clone(QueryExpression query)
        {
            QueryExpression copy = new QueryExpression(query.EntityName)
            {
                ColumnSet = query.ColumnSet == null ? null : Clone(query.ColumnSet),
                Criteria = Clone(query.Criteria),
                Distinct = query.Distinct,
                NoLock = query.NoLock,
                TopCount = query.TopCount,
                PageInfo = Clone(query.PageInfo),
            };
            copy.Orders.AddRange(query.Orders.Select(Clone));
            copy.LinkEntities.AddRange(query.LinkEntities.Select(Clone));
            return copy;
        }

        public static QueryByAttribute Clone(QueryByAttribute query)
        {
            QueryByAttribute copy = new QueryByAttribute(query.EntityName)
            {
                ColumnSet = query.ColumnSet == null ? null : Clone(query.ColumnSet),
                TopCount = query.TopCount,
                PageInfo = Clone(query.PageInfo),
            };
            copy.Attributes.AddRange(query.Attributes);
            copy.Values.AddRange(query.Values.Select(v => EntityValues.CloneValue(v)));
            copy.Orders.AddRange(query.Orders.Select(Clone));
            return copy;
        }

        private static FilterExpression Clone(FilterExpression filter)
        {
            FilterExpression copy = new FilterExpression(filter.FilterOperator)
            {
                FilterHint = filter.FilterHint,
                IsQuickFindFilter = filter.IsQuickFindFilter,
                AnyAllFilterLinkEntity = filter.AnyAllFilterLinkEntity == null ? null : Clone(filter.AnyAllFilterLinkEntity),
            };
            copy.Conditions.AddRange(filter.Conditions.Select(Clone));
            copy.Filters.AddRange(filter.Filters.Select(Clone));
            return copy;
        }

        private static ConditionExpression Clone(ConditionExpression condition)
        {
            ConditionExpression copy = new ConditionExpression(condition.EntityName, condition.AttributeName, condition.Operator)
            {
                CompareColumns = condition.CompareColumns,
            };
            copy.Values.AddRange(condition.Values.Select(v => EntityValues.CloneValue(v)));
            return copy;
        }

        private static OrderExpression Clone(OrderExpression order)
            => new OrderExpression(order.AttributeName, order.OrderType) { Alias = order.Alias, EntityName = order.EntityName };

        private static LinkEntity Clone(LinkEntity link)
        {
            LinkEntity copy = new LinkEntity(link.LinkFromEntityName, link.LinkToEntityName, link.LinkFromAttributeName, link.LinkToAttributeName, link.JoinOperator)
            {
                EntityAlias = link.EntityAlias,
                Columns = link.Columns == null ? null : Clone(link.Columns),
                LinkCriteria = Clone(link.LinkCriteria),
            };
            copy.Orders.AddRange(link.Orders.Select(Clone));
            copy.LinkEntities.AddRange(link.LinkEntities.Select(Clone));
            return copy;
        }

        private static PagingInfo? Clone(PagingInfo? paging)
            => paging == null
                ? null
                : new PagingInfo
                {
                    Count = paging.Count,
                    PageNumber = paging.PageNumber,
                    PagingCookie = paging.PagingCookie,
                    ReturnTotalRecordCount = paging.ReturnTotalRecordCount,
                };
    }
}
