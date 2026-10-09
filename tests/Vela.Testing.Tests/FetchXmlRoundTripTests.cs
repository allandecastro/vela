using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    [EntityLogicalName("account")]
    public sealed class RtAccount : Entity
    {
        public RtAccount()
            : base("account")
        {
        }

        [AttributeLogicalName("accountid")]
        public Guid? AccountId => GetAttributeValue<Guid?>("accountid");

        [AttributeLogicalName("name")]
        public string Name => GetAttributeValue<string>("name");

        [AttributeLogicalName("numberofemployees")]
        public int? NumberOfEmployees => GetAttributeValue<int?>("numberofemployees");

        [AttributeLogicalName("new_target")]
        public int? Target => GetAttributeValue<int?>("new_target");

        [AttributeLogicalName("revenue")]
        public Money Revenue => GetAttributeValue<Money>("revenue");

        [AttributeLogicalName("createdon")]
        public DateTime? CreatedOn => GetAttributeValue<DateTime?>("createdon");
    }

    [EntityLogicalName("contact")]
    public sealed class RtContact : Entity
    {
        public RtContact()
            : base("contact")
        {
        }

        [AttributeLogicalName("contactid")]
        public Guid? ContactId => GetAttributeValue<Guid?>("contactid");

        [AttributeLogicalName("fullname")]
        public string FullName => GetAttributeValue<string>("fullname");

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference ParentCustomerId => GetAttributeValue<EntityReference>("parentcustomerid");

        [AttributeLogicalName("statecode")]
        public OptionSetValue StateCode => GetAttributeValue<OptionSetValue>("statecode");

        [AttributeLogicalName("jobtitle")]
        public string JobTitle => GetAttributeValue<string>("jobtitle");
    }

    // A-19: joins and conditions in FetchXML used to be silently ignored or mis-mapped by the harness parser (every
    // link-type but outer became inner, entityname/valueof were never read, link-entities in filters and under
    // aggregates were dropped). Each construct is now asserted on REAL rows twice over the same seeded data: once as
    // a QueryExpression and once as its ToFetchXml() run through the FetchXML parser, which must agree exactly.
    public sealed class FetchXmlRoundTripTests
    {
        private static readonly Guid Acme = new Guid("a0000000-0000-0000-0000-000000000001");
        private static readonly Guid Globex = new Guid("a0000000-0000-0000-0000-000000000002");
        private static readonly Guid Initech = new Guid("a0000000-0000-0000-0000-000000000003");
        private static readonly Guid Umbrella = new Guid("a0000000-0000-0000-0000-000000000004");
        private static readonly Guid Hooli = new Guid("a0000000-0000-0000-0000-000000000005");
        private static readonly Guid Wile = new Guid("c0000000-0000-0000-0000-000000000001");
        private static readonly Guid Road = new Guid("c0000000-0000-0000-0000-000000000002");
        private static readonly Guid Hank = new Guid("c0000000-0000-0000-0000-000000000003");
        private static readonly Guid Gail = new Guid("c0000000-0000-0000-0000-000000000004");
        private static readonly Guid Orphan = new Guid("c0000000-0000-0000-0000-000000000005");

        // Accounts: Acme & Globex have contacts, Initech/Umbrella/Hooli have none. Contacts: Wile (active) and Road
        // (inactive) under Acme; Hank and Gail (active) under Globex; Orphan has no parent. Tasks: Call->Wile,
        // Mail->Road, Fax->Gail (Hank has none, so Globex's FIRST contact has no task).
        private static InMemoryOrganizationService Org()
        {
            return new InMemoryOrganizationService().Seed(
                Account(Acme, "Acme", 10, 5, 100m, new DateTime(2024, 1, 15)),
                Account(Globex, "Globex", 20, 20, 200m, new DateTime(2024, 3, 20)),
                Account(Initech, "Initech", 30, 40, null, new DateTime(2024, 5, 3)),
                Account(Umbrella, "Umbrella", 40, null, 400m, new DateTime(2025, 1, 7)),
                Account(Hooli, "Hooli", null, 60, 500m, null),
                Contact(Wile, "Wile", Acme, 0, "Dev", "dev"),
                Contact(Road, "Road", Acme, 1, "dev", "x"),
                Contact(Hank, "Hank", Globex, 0, "QA", "QA"),
                Contact(Gail, "Gail", Globex, 0, null, null),
                Contact(Orphan, "Orphan", null, 0, "Ops", null),
                Task("Call", Wile),
                Task("Mail", Road),
                Task("Fax", Gail));
        }

        private static Entity Account(Guid id, string name, int? employees, int? target, decimal? revenue, DateTime? createdOn)
        {
            Entity account = new Entity("account", id) { ["name"] = name };
            if (employees.HasValue)
            {
                account["numberofemployees"] = employees.Value;
            }

            if (target.HasValue)
            {
                account["new_target"] = target.Value;
            }

            if (revenue.HasValue)
            {
                account["revenue"] = new Money(revenue.Value);
            }

            if (createdOn.HasValue)
            {
                account["createdon"] = createdOn.Value;
            }

            return account;
        }

        private static Entity Contact(Guid id, string name, Guid? parent, int state, string? jobTitle, string? nickName)
        {
            Entity contact = new Entity("contact", id) { ["fullname"] = name, ["statecode"] = new OptionSetValue(state) };
            if (parent.HasValue)
            {
                contact["parentcustomerid"] = new EntityReference("account", parent.Value);
            }

            if (jobTitle != null)
            {
                contact["jobtitle"] = jobTitle;
            }

            if (nickName != null)
            {
                contact["nickname"] = nickName;
            }

            return contact;
        }

        private static Entity Task(string subject, Guid regarding)
        {
            return new Entity("task", Guid.NewGuid()) { ["subject"] = subject, ["regardingobjectid"] = new EntityReference("contact", regarding) };
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        // Runs the QueryExpression and its FetchXML through the harness, asserts both return the same rows (same
        // ids, columns and values; in order when ordered) and returns the QueryExpression result.
        private static EntityCollection Both(InMemoryOrganizationService org, QueryExpression query, string? fetchXml = null, bool ordered = false)
        {
            EntityCollection viaQuery = org.RetrieveMultiple(query);
            EntityCollection viaFetch = org.RetrieveMultiple(new FetchExpression(fetchXml ?? query.ToFetchXml()));
            IEnumerable<string> left = viaQuery.Entities.Select(Signature);
            IEnumerable<string> right = viaFetch.Entities.Select(Signature);
            if (!ordered)
            {
                left = left.OrderBy(s => s, StringComparer.Ordinal);
                right = right.OrderBy(s => s, StringComparer.Ordinal);
            }

            Assert.Equal(left.ToArray(), right.ToArray());
            Assert.Equal(viaQuery.MoreRecords, viaFetch.MoreRecords);
            return viaQuery;
        }

        private static EntityCollection Both<T>(InMemoryOrganizationService org, TypedQuery<T> query, bool ordered = false)
            where T : Entity
        {
            return Both(org, (QueryExpression)query, query.ToFetchXml(), ordered);
        }

        private static string Signature(Entity entity)
        {
            return entity.Id + "{" + string.Join(",", entity.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + Text(a.Value))) + "}";
        }

        private static string Text(object? value)
        {
            switch (value)
            {
                case null: return "<null>";
                case AliasedValue aliased: return aliased.EntityLogicalName + "." + aliased.AttributeLogicalName + ":" + Text(aliased.Value);
                case EntityReference reference: return reference.LogicalName + ":" + reference.Id;
                case Money money: return "$" + money.Value.ToString(CultureInfo.InvariantCulture);
                case OptionSetValue option: return "#" + option.Value.ToString(CultureInfo.InvariantCulture);
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? string.Empty;
            }
        }

        private static string Name(Entity entity) => entity.GetAttributeValue<string>("name");

        private static string Aliased(Entity entity, string key)
        {
            return entity.Contains(key) ? Text(entity.GetAttributeValue<AliasedValue>(key).Value) : "-";
        }

        private static string[] Sorted(EntityCollection rows, Func<Entity, string> project)
        {
            return rows.Entities.Select(project).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }

        private static string[] InOrder(EntityCollection rows, Func<Entity, string> project)
        {
            return rows.Entities.Select(project).ToArray();
        }

        private static string[] Names(EntityCollection rows) => Sorted(rows, Name);

        private static EntityCollection Fetch(InMemoryOrganizationService org, string fetchXml)
        {
            return org.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        private static QueryExpression Accounts(params string[] columns)
        {
            return new QueryExpression("account") { ColumnSet = new ColumnSet(columns) };
        }

        private static LinkEntity ContactLink(QueryExpression query, JoinOperator join, string alias = "c", params string[] columns)
        {
            LinkEntity link = query.AddLink("contact", "accountid", "parentcustomerid", join);
            link.EntityAlias = alias;
            link.Columns = new ColumnSet(columns);
            return link;
        }

        private static LinkEntity TaskLink(LinkEntity contact, JoinOperator join, string alias = "t", params string[] columns)
        {
            LinkEntity link = contact.AddLink("task", "contactid", "regardingobjectid", join);
            link.EntityAlias = alias;
            link.Columns = new ColumnSet(columns);
            return link;
        }

        private static object? Value(Entity row, string alias) => row.GetAttributeValue<AliasedValue>(alias).Value;

        // ---- joins ---------------------------------------------------------------------------------------------

        [Fact]
        public void Inner_join_returns_one_row_per_linked_record_with_its_columns()
        {
            EntityCollection rows = Both(Org(), Query.For<RtAccount>().Select(a => a.Name)
                .Link<RtContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.Inner, "c", l => l.Select(c => c.FullName)));

            Assert.Equal(new[] { "Acme|Road", "Acme|Wile", "Globex|Gail", "Globex|Hank" }, Sorted(rows, r => Name(r) + "|" + Aliased(r, "c.fullname")));
        }

        [Fact]
        public void Explicit_inner_link_type_is_an_inner_join()
        {
            string fetch = "<fetch><entity name='account'><attribute name='name'/>"
                + "<link-entity name='contact' from='parentcustomerid' to='accountid' link-type='inner' alias='c'/></entity></fetch>";

            Assert.Equal(new[] { "Acme", "Acme", "Globex", "Globex" }, Names(Fetch(Org(), fetch)));
        }

        [Fact]
        public void Outer_join_keeps_unmatched_parents_without_linked_columns()
        {
            EntityCollection rows = Both(Org(), Query.For<RtAccount>().Select(a => a.Name)
                .Link<RtContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.LeftOuter, "c", l => l.Select(c => c.FullName)));

            Assert.Equal(
                new[] { "Acme|Road", "Acme|Wile", "Globex|Gail", "Globex|Hank", "Hooli|-", "Initech|-", "Umbrella|-" },
                Sorted(rows, r => Name(r) + "|" + Aliased(r, "c.fullname")));
        }

        [Fact]
        public void Outer_join_null_pattern_finds_records_not_in_a_set()
        {
            // entityname on the condition targets the link alias: accounts with NO contact (contactid null on the
            // outer join). The link selects no columns, so the condition must read the linked record itself.
            TypedQuery<RtAccount> query = Query.For<RtAccount>().Select(a => a.Name)
                .Link<RtContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.LeftOuter, "c")
                .WhereLink<RtContact>("c", c => c.ContactId, ConditionOperator.Null);

            Assert.Contains("entityname=\"c\"", query.ToFetchXml());
            Assert.Equal(new[] { "Hooli", "Initech", "Umbrella" }, Names(Both(Org(), query)));
        }

        [Fact]
        public void Entityname_condition_reads_an_unselected_linked_column()
        {
            TypedQuery<RtAccount> query = Query.For<RtAccount>().Select(a => a.Name)
                .Link<RtContact>(a => a.AccountId, c => c.ParentCustomerId, JoinOperator.Inner, "c")
                .WhereLink<RtContact>("c", c => c.FullName, ConditionOperator.Equal, "Hank");

            Entity row = Assert.Single(Both(Org(), query).Entities);
            Assert.Equal("Globex", Name(row));

            // Nothing from the link is returned: neither the unselected column nor the join key (only the selected
            // column and the primary key, which Dataverse always returns).
            Assert.Equal(new[] { "name", "accountid" }, row.Attributes.Keys.ToArray());
        }

        [Fact]
        public void Entityname_condition_on_an_unknown_alias_reads_null()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner);
            query.Criteria.AddCondition("nope", "fullname", ConditionOperator.Null);

            Assert.Equal(new[] { "Acme", "Acme", "Globex", "Globex" }, Names(Both(Org(), query)));
        }

        [Fact]
        public void Exists_and_in_return_each_matching_parent_once_without_columns()
        {
            foreach (JoinOperator join in new[] { JoinOperator.Exists, JoinOperator.In })
            {
                QueryExpression all = Accounts("name");
                ContactLink(all, join, "c", "fullname");
                EntityCollection rows = Both(Org(), all);
                Assert.Equal(new[] { "Acme", "Globex" }, Names(rows));
                Assert.All(rows.Entities, r => Assert.False(r.Contains("c.fullname")));

                QueryExpression inactive = Accounts("name");
                ContactLink(inactive, join).LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 1);
                Assert.Equal(new[] { "Acme" }, Names(Both(Org(), inactive)));
            }
        }

        [Fact]
        public void Exists_with_a_nested_link_only_counts_linked_rows_that_match_it()
        {
            QueryExpression query = Accounts("name");
            TaskLink(ContactLink(query, JoinOperator.Exists), JoinOperator.Inner).LinkCriteria.AddCondition("subject", ConditionOperator.Equal, "Fax");

            Assert.Equal(new[] { "Globex" }, Names(Both(Org(), query)));
        }

        [Fact]
        public void Match_first_row_is_an_inner_variant_returning_the_first_match_once()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.MatchFirstRowUsingCrossApply, "c", "fullname");

            Assert.Equal(new[] { "Acme|Wile", "Globex|Hank" }, Sorted(Both(Org(), query), r => Name(r) + "|" + Aliased(r, "c.fullname")));
        }

        [Fact]
        public void Match_first_row_picks_the_first_linked_row_that_satisfies_its_nested_link()
        {
            // Globex's first contact (Hank) has no task, so the first match through the nested inner link is Gail.
            QueryExpression query = Accounts("name");
            TaskLink(ContactLink(query, JoinOperator.MatchFirstRowUsingCrossApply, "c", "fullname"), JoinOperator.Inner, "t", "subject");

            Assert.Equal(
                new[] { "Acme|Wile|Call", "Globex|Gail|Fax" },
                Sorted(Both(Org(), query), r => Name(r) + "|" + Aliased(r, "c.fullname") + "|" + Aliased(r, "t.subject")));
        }

        [Fact]
        public void Nested_inner_links_join_three_tables()
        {
            QueryExpression query = Accounts("name");
            TaskLink(ContactLink(query, JoinOperator.Inner, "c", "fullname"), JoinOperator.Inner, "t", "subject");

            Assert.Equal(
                new[] { "Acme|Road|Mail", "Acme|Wile|Call", "Globex|Gail|Fax" },
                Sorted(Both(Org(), query), r => Name(r) + "|" + Aliased(r, "c.fullname") + "|" + Aliased(r, "t.subject")));
        }

        [Theory]
        [InlineData(JoinOperator.Any, new[] { "Acme" })]
        [InlineData(JoinOperator.NotAny, new[] { "Globex", "Hooli", "Initech", "Umbrella" })]
        [InlineData(JoinOperator.All, new[] { "Globex" })]   // has contacts, none of which is inactive
        [InlineData(JoinOperator.NotAll, new[] { "Acme" })]  // equivalent to Any
        public void Related_record_filters_keep_the_right_parents(JoinOperator join, string[] expected)
        {
            TypedQuery<RtAccount> query = Query.For<RtAccount>().Select(a => a.Name)
                .WhereRelated<RtContact>(a => a.AccountId, c => c.ParentCustomerId, join, l => l.Where(c => c.StateCode, ConditionOperator.Equal, 1));

            Assert.Equal(expected, Names(Both(Org(), query)));
        }

        [Theory]
        [InlineData(JoinOperator.Any, new[] { "Acme" })]
        [InlineData(JoinOperator.NotAny, new[] { "Globex", "Hooli", "Initech", "Umbrella" })]
        [InlineData(JoinOperator.All, new[] { "Globex" })]
        [InlineData(JoinOperator.NotAll, new[] { "Acme" })]
        public void Related_record_link_types_used_as_a_join_filter_the_parent(JoinOperator join, string[] expected)
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, join).LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 1);

            EntityCollection rows = Both(Org(), query);
            Assert.Equal(expected, Names(rows));
            Assert.All(rows.Entities, r => Assert.Equal(new[] { "name", "accountid" }, r.Attributes.Keys.ToArray()));
        }

        [Fact]
        public void Related_record_filter_honours_its_nested_link()
        {
            QueryExpression query = Accounts("name");
            LinkEntity any = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.Any);
            TaskLink(any, JoinOperator.Inner).LinkCriteria.AddCondition("subject", ConditionOperator.Equal, "Fax");
            query.Criteria.AnyAllFilterLinkEntity = any;

            Assert.Equal(new[] { "Globex" }, Names(Both(Org(), query)));
        }

        [Fact]
        public void Several_related_record_filters_in_one_filter_combine_with_its_operator()
        {
            string Link(string condition) =>
                "<link-entity name='contact' from='parentcustomerid' to='accountid' link-type='any'><filter>" + condition + "</filter></link-entity>";
            string hank = Link("<condition attribute='fullname' operator='eq' value='Hank'/>");
            string inactive = Link("<condition attribute='statecode' operator='eq' value='1'/>");

            string or = "<fetch><entity name='account'><attribute name='name'/><filter type='or'>" + hank + inactive + "</filter></entity></fetch>";
            string and = "<fetch><entity name='account'><attribute name='name'/><filter type='and'>" + hank + inactive + "</filter></entity></fetch>";

            Assert.Equal(new[] { "Acme", "Globex" }, Names(Fetch(Org(), or)));
            Assert.Empty(Fetch(Org(), and).Entities);
        }

        // ---- column comparison (valueof) ---------------------------------------------------------------------

        [Theory]
        [InlineData(ConditionOperator.Equal, new[] { "Globex" })]
        [InlineData(ConditionOperator.NotEqual, new[] { "Acme", "Initech" })] // Umbrella (null target) and Hooli (null employees) excluded
        [InlineData(ConditionOperator.GreaterThan, new[] { "Acme" })]
        [InlineData(ConditionOperator.GreaterEqual, new[] { "Acme", "Globex" })]
        [InlineData(ConditionOperator.LessThan, new[] { "Initech" })]
        [InlineData(ConditionOperator.LessEqual, new[] { "Globex", "Initech" })]
        public void Column_comparison_compares_two_columns_of_the_row(ConditionOperator op, string[] expected)
        {
            TypedQuery<RtAccount> query = Query.For<RtAccount>().Select(a => a.Name).WhereColumns(a => a.NumberOfEmployees, op, a => a.Target);

            Assert.Contains("valueof=\"new_target\"", query.ToFetchXml());
            Assert.Equal(expected, Names(Both(Org(), query)));
        }

        [Theory]
        [InlineData("nickname")]
        [InlineData("c.nickname")]
        public void Column_comparison_on_a_linked_entity_reads_the_linked_columns(string valueOf)
        {
            // Wile (jobtitle Dev, nickname dev: equal case-insensitively) and Hank (QA/QA) match; Road and Gail don't.
            string fetch = "<fetch><entity name='account'><attribute name='name'/>"
                + "<link-entity name='contact' from='parentcustomerid' to='accountid' alias='c'><attribute name='fullname'/></link-entity>"
                + $"<filter><condition entityname='c' attribute='jobtitle' operator='eq' valueof='{valueOf}'/></filter></entity></fetch>";

            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner, "c", "fullname");
            query.Criteria.AddCondition(new ConditionExpression("jobtitle", ConditionOperator.Equal, true, valueOf) { EntityName = "c" });

            Assert.Equal(new[] { "Acme|Wile", "Globex|Hank" }, Sorted(Both(Org(), query, fetch), r => Name(r) + "|" + Aliased(r, "c.fullname")));
        }

        [Fact]
        public void Column_comparison_with_an_unsupported_operator_throws()
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Fetch(Org(),
                "<fetch><entity name='account'><filter><condition attribute='name' operator='like' valueof='new_target'/></filter></entity></fetch>"));
            Assert.Contains("'Like'", error.Message);
        }

        [Fact]
        public void Column_comparison_must_name_exactly_one_column()
        {
            QueryExpression query = Accounts("name");
            query.Criteria.AddCondition(new ConditionExpression("numberofemployees", ConditionOperator.Equal, "new_target", "name") { CompareColumns = true });

            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(query));
            Assert.Contains("'numberofemployees'", error.Message);
        }

        // ---- ordering ----------------------------------------------------------------------------------------

        [Fact]
        public void Link_orders_sort_by_the_linked_column()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner, "c", "fullname").Orders.Add(new OrderExpression("fullname", OrderType.Ascending));

            Assert.Equal(new[] { "Globex", "Globex", "Acme", "Acme" }, InOrder(Both(Org(), query, ordered: true), Name));
        }

        [Fact]
        public void Link_orders_sort_by_an_unselected_linked_column()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner).Orders.Add(new OrderExpression("fullname", OrderType.Descending));

            Assert.Equal(new[] { "Acme", "Acme", "Globex", "Globex" }, InOrder(Both(Org(), query, ordered: true), Name));

            QueryExpression ascending = Accounts("name");
            ContactLink(ascending, JoinOperator.Inner).Orders.Add(new OrderExpression("fullname", OrderType.Ascending));
            Assert.Equal(new[] { "Globex", "Globex", "Acme", "Acme" }, InOrder(Both(Org(), ascending, ordered: true), Name));
        }

        [Fact]
        public void Root_order_with_entityname_sorts_by_the_linked_column()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner, "c", "fullname");
            query.Orders.Add(new OrderExpression { EntityName = "c", AttributeName = "fullname", OrderType = OrderType.Ascending });

            Assert.Contains("entityname=\"c\"", query.ToFetchXml());
            Assert.Equal(new[] { "Gail", "Hank", "Road", "Wile" }, InOrder(Both(Org(), query, ordered: true), r => Aliased(r, "c.fullname")));
        }

        // ---- column aliases --------------------------------------------------------------------------------------

        [Fact]
        public void Column_aliases_are_a_plain_fetch_not_an_aggregate()
        {
            QueryExpression query = Accounts();
            query.Criteria.AddCondition("name", ConditionOperator.Equal, "Acme");
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "name", Alias = "nm" });

            Assert.DoesNotContain("aggregate", query.ToFetchXml());
            Entity row = Assert.Single(Both(Org(), query).Entities);
            Assert.Equal("Acme", Value(row, "nm"));
            Assert.False(row.Contains("name"));
        }

        [Fact]
        public void Column_alias_inside_a_link_entity_is_returned_under_the_alias()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Inner).Columns.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "jobtitle", Alias = "job" });

            EntityCollection rows = Both(Org(), query);
            // Gail has no job title, so her row carries no "job" column.
            Assert.Equal(new[] { "Acme|Dev", "Acme|dev", "Globex|-", "Globex|QA" }, Sorted(rows, r => Name(r) + "|" + Aliased(r, "job")));
            Assert.Equal("contact", rows.Entities.First(r => r.Contains("job")).GetAttributeValue<AliasedValue>("job").EntityLogicalName);
        }

        // ---- aggregates over joins -----------------------------------------------------------------------------

        private static QueryExpression Aggregate(string entity = "account")
        {
            return new QueryExpression(entity) { ColumnSet = new ColumnSet() };
        }

        private static XrmAttributeExpression Agg(string attribute, XrmAggregateType type, string alias)
        {
            return new XrmAttributeExpression { AttributeName = attribute, AggregateType = type, Alias = alias };
        }

        private static XrmAttributeExpression Group(string attribute, string alias, XrmDateTimeGrouping grouping = XrmDateTimeGrouping.None)
        {
            return new XrmAttributeExpression { AttributeName = attribute, Alias = alias, HasGroupBy = true, DateTimeGrouping = grouping };
        }

        [Fact]
        public void Aggregate_over_an_inner_join_counts_the_joined_rows_per_group()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("name", "nm"));
            LinkEntity contacts = ContactLink(query, JoinOperator.Inner);
            contacts.LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            contacts.Columns.AttributeExpressions.Add(Agg("fullname", XrmAggregateType.CountColumn, "n"));

            EntityCollection rows = Both(Org(), query);
            Assert.Equal(new[] { "Acme=1", "Globex=2" }, Sorted(rows, r => Value(r, "nm") + "=" + Value(r, "n")));
            AliasedValue count = rows.Entities[0].GetAttributeValue<AliasedValue>("n");
            Assert.Equal("contact", count.EntityLogicalName);
            Assert.Equal("fullname", count.AttributeLogicalName);
            Assert.Equal("account", rows.Entities[0].GetAttributeValue<AliasedValue>("nm").EntityLogicalName);
        }

        [Fact]
        public void Aggregate_over_an_outer_join_counts_unmatched_parents_once()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "rows"));
            ContactLink(query, JoinOperator.LeftOuter).Columns.AttributeExpressions.Add(Agg("fullname", XrmAggregateType.CountColumn, "named"));

            Entity row = Assert.Single(Both(Org(), query).Entities);
            Assert.Equal(7, Value(row, "rows"));  // 4 joined contacts + 3 contact-less accounts
            Assert.Equal(4, Value(row, "named")); // only the joined rows carry a contact name
        }

        [Fact]
        public void Aggregate_can_group_by_a_linked_column()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(query, JoinOperator.Inner, "lnk").Columns.AttributeExpressions.Add(Group("statecode", "state"));

            EntityCollection rows = Both(Org(), query);
            Assert.Equal(new[] { "#0=3", "#1=1" }, Sorted(rows, r => Text(Value(r, "state")) + "=" + Value(r, "n")));
            Assert.Equal("contact", rows.Entities[0].GetAttributeValue<AliasedValue>("state").EntityLogicalName);
        }

        [Fact]
        public void Aggregate_on_a_link_without_alias_reads_the_column_under_the_table_name()
        {
            QueryExpression query = Aggregate();
            LinkEntity link = query.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.Inner);
            link.Columns.AttributeExpressions.Add(Agg("jobtitle", XrmAggregateType.CountColumn, "jobs"));

            Assert.Equal(3, Value(Assert.Single(Both(Org(), query).Entities), "jobs")); // Gail has none
        }

        [Fact]
        public void Aggregate_only_on_a_nested_link_is_still_an_aggregate()
        {
            QueryExpression query = Aggregate();
            TaskLink(ContactLink(query, JoinOperator.Inner), JoinOperator.Inner).Columns.AttributeExpressions.Add(Agg("subject", XrmAggregateType.CountColumn, "tasks"));

            Assert.Contains("aggregate=\"true\"", query.ToFetchXml());
            Assert.Equal(3, Value(Assert.Single(Both(Org(), query).Entities), "tasks"));
        }

        [Fact]
        public void Aggregate_honours_the_outer_join_null_pattern_and_related_record_filters()
        {
            QueryExpression orphans = Aggregate();
            orphans.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(orphans, JoinOperator.LeftOuter);
            orphans.Criteria.AddCondition("c", "contactid", ConditionOperator.Null);
            Assert.Equal(3, Value(Assert.Single(Both(Org(), orphans).Entities), "n"));

            QueryExpression withContacts = Aggregate();
            withContacts.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            withContacts.Criteria.AnyAllFilterLinkEntity = new LinkEntity("account", "contact", "accountid", "parentcustomerid", JoinOperator.Any);
            Assert.Equal(2, Value(Assert.Single(Both(Org(), withContacts).Entities), "n"));
        }

        [Fact]
        public void Aggregate_functions_over_money_with_nulls()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "count"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.CountColumn, "withrevenue"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Sum, "sum"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Avg, "avg"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Min, "min"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Max, "max"));

            Entity row = Assert.Single(Both(Org(), query).Entities);
            Assert.Equal(5, Value(row, "count"));
            Assert.Equal(4, Value(row, "withrevenue")); // Initech has no revenue
            Assert.Equal(1200m, Value(row, "sum"));
            Assert.Equal(300m, Value(row, "avg"));
            Assert.Equal(100m, ((Money)Value(row, "min")!).Value);
            Assert.Equal(500m, ((Money)Value(row, "max")!).Value);
        }

        [Fact]
        public void Aggregate_values_over_no_rows_are_null_and_count_is_zero()
        {
            QueryExpression query = Aggregate();
            query.Criteria.AddCondition("name", ConditionOperator.Equal, "nobody");
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "count"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.CountColumn, "cc"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Sum, "sum"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Avg, "avg"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Min, "min"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Max, "max"));

            Entity row = Assert.Single(Both(Org(), query).Entities);
            Assert.Equal(0, Value(row, "count"));
            Assert.Equal(0, Value(row, "cc"));
            Assert.Null(Value(row, "sum"));
            Assert.Null(Value(row, "avg"));
            Assert.Null(Value(row, "min"));
            Assert.Null(Value(row, "max"));
        }

        [Fact]
        public void Aggregate_min_max_and_group_values_are_copies_not_the_stored_values()
        {
            InMemoryOrganizationService org = Org();
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(query, JoinOperator.Inner).Columns.AttributeExpressions.Add(Group("parentcustomerid", "parent"));

            foreach (Entity row in org.RetrieveMultiple(query).Entities)
            {
                ((EntityReference)Value(row, "parent")!).Id = Guid.Empty; // mutate the result
            }

            Assert.Equal(Acme, org.Retrieve("contact", Wile, new ColumnSet("parentcustomerid")).GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        [Fact]
        public void Aggregate_orders_by_alias_on_the_QueryExpression_path_too()
        {
            TypedQuery<RtAccount> query = Query.For<RtAccount>().GroupBy(a => a.Name, "nm").Count(a => a.AccountId, "n").OrderByAggregate("nm", descending: true);

            Assert.Equal(
                new[] { "Umbrella", "Initech", "Hooli", "Globex", "Acme" },
                InOrder(Both(Org(), query, ordered: true), r => (string)Value(r, "nm")!));

            TypedQuery<RtAccount> ascending = Query.For<RtAccount>().GroupBy(a => a.Name, "nm").Count(a => a.AccountId, "n").OrderByAggregate("nm");
            Assert.Equal(
                new[] { "Acme", "Globex", "Hooli", "Initech", "Umbrella" },
                InOrder(Both(Org(), ascending, ordered: true), r => (string)Value(r, "nm")!));
        }

        [Fact]
        public void Aggregate_order_by_attribute_naming_an_alias_is_accepted()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("name", "nm"));
            query.AddOrder("nm", OrderType.Descending);

            Assert.Equal("Umbrella", Value(Both(Org(), query, ordered: true).Entities[0], "nm"));
        }

        [Fact]
        public void Aggregate_results_are_paged()
        {
            TypedQuery<RtAccount> page2 = Query.For<RtAccount>().GroupBy(a => a.Name, "nm").Count(a => a.AccountId, "n").OrderByAggregate("nm").Page(2, 2);
            EntityCollection second = Both(Org(), page2, ordered: true);
            Assert.Equal(new[] { "Hooli", "Initech" }, InOrder(second, r => (string)Value(r, "nm")!));
            Assert.True(second.MoreRecords);

            TypedQuery<RtAccount> page3 = Query.For<RtAccount>().GroupBy(a => a.Name, "nm").Count(a => a.AccountId, "n").OrderByAggregate("nm").Page(3, 2);
            EntityCollection third = Both(Org(), page3, ordered: true);
            Assert.Equal(new[] { "Umbrella" }, InOrder(third, r => (string)Value(r, "nm")!));
            Assert.False(third.MoreRecords);

            TypedQuery<RtAccount> top = Query.For<RtAccount>().GroupBy(a => a.Name, "nm").Count(a => a.AccountId, "n").OrderByAggregate("nm").Top(1);
            Assert.Equal(new[] { "Acme" }, InOrder(Both(Org(), top, ordered: true), r => (string)Value(r, "nm")!));
        }

        [Theory]
        [InlineData(XrmDateTimeGrouping.Year, "dategrouping=\"year\"", new[] { "-=1", "2024=3", "2025=1" })]
        [InlineData(XrmDateTimeGrouping.Quarter, "dategrouping=\"quarter\"", new[] { "-=1", "1=3", "2=1" })]
        [InlineData(XrmDateTimeGrouping.Month, "dategrouping=\"month\"", new[] { "-=1", "1=2", "3=1", "5=1" })]
        [InlineData(XrmDateTimeGrouping.Day, "dategrouping=\"day\"", new[] { "-=1", "15=1", "20=1", "3=1", "7=1" })]
        public void Date_grouping_groups_by_the_calendar_part(XrmDateTimeGrouping grouping, string emitted, string[] expected)
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("createdon", "part", grouping));
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));

            Assert.Contains(emitted, query.ToFetchXml());
            EntityCollection rows = Both(Org(), query);
            Assert.Equal(expected, Sorted(rows, r => (r.GetAttributeValue<AliasedValue>("part").Value is int part ? part.ToString(CultureInfo.InvariantCulture) : "-") + "=" + Value(r, "n")));
        }

        [Theory]
        [InlineData(XrmDateTimeGrouping.Week, "week")]
        [InlineData(XrmDateTimeGrouping.FiscalPeriod, "fiscal-period")]
        [InlineData(XrmDateTimeGrouping.FiscalYear, "fiscal-year")]
        public void Calendar_dependent_date_groupings_are_rejected(XrmDateTimeGrouping grouping, string fetchName)
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("createdon", "part", grouping));

            Assert.Contains($"'{grouping}'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(query)).Message);
            string fetch = $"<fetch aggregate='true'><entity name='account'><attribute name='createdon' alias='p' groupby='true' dategrouping='{fetchName}'/></entity></fetch>";
            Assert.Contains($"'{grouping}'", Assert.Throws<NotSupportedException>(() => Fetch(Org(), fetch)).Message);
        }

        [Fact]
        public void Date_grouping_a_non_date_column_throws()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("name", "part", XrmDateTimeGrouping.Year));

            Assert.Contains("date/time", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(query)).Message);
        }

        [Fact]
        public void Usertimezone_false_groups_by_the_stored_utc_value()
        {
            string fetch = "<fetch aggregate='true'><entity name='account'>"
                + "<attribute name='createdon' alias='y' groupby='true' dategrouping='year' usertimezone='false'/>"
                + "<attribute name='accountid' alias='n' aggregate='count'/></entity></fetch>";

            Assert.Equal(new[] { "-=1", "2024=3", "2025=1" }, Sorted(Fetch(Org(), fetch), r => (Value(r, "y")?.ToString() ?? "-") + "=" + Value(r, "n")));
        }

        [Fact]
        public void Distinct_countcolumn_counts_unique_values_case_insensitively()
        {
            TypedQuery<RtContact> query = Query.For<RtContact>().Count(c => c.ContactId, "all").CountColumnDistinct(c => c.JobTitle, "unique");

            Entity row = Assert.Single(Fetch(Org(), query.ToFetchXml()).Entities);
            Assert.Equal(5, Value(row, "all"));
            Assert.Equal(3, Value(row, "unique")); // Dev/dev, QA, Ops (Gail has none)

            // distinct is FetchXML-only: the QueryExpression form is a plain countcolumn.
            Assert.Equal(4, Value(Assert.Single(Org().RetrieveMultiple(query).Entities), "unique"));
        }

        [Fact]
        public void Distinct_false_on_countcolumn_counts_every_value()
        {
            string fetch = "<fetch aggregate='true'><entity name='contact'><attribute name='jobtitle' alias='n' aggregate='countcolumn' distinct='false'/></entity></fetch>";
            Assert.Equal(4, Value(Assert.Single(Fetch(Org(), fetch).Entities), "n"));
        }

        [Fact]
        public void Aggregate_limit_is_enforced_at_its_boundary()
        {
            string Limited(int limit) => Query.For<RtAccount>().Count(a => a.AccountId, "n").AggregateLimit(limit).ToFetchXml();

            Assert.Equal(5, Value(Assert.Single(Fetch(Org(), Limited(5)).Entities), "n")); // exactly at the limit
            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch(Org(), Limited(4)));
            Assert.Equal(unchecked((int)0x8004E023), fault.Detail.ErrorCode);
            Assert.Contains("AggregateQueryRecordLimit", fault.Detail.Message);
            Assert.Contains("AggregateQueryRecordLimit", fault.Message);
        }

        [Fact]
        public void Aggregate_can_order_by_an_aggregate_alias()
        {
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Group("name", "nm"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Sum, "s"));
            query.Orders.Add(new OrderExpression { Alias = "s", OrderType = OrderType.Descending });

            Assert.Equal(
                new[] { "Hooli", "Umbrella", "Globex", "Acme", "Initech" }, // Initech has no revenue (null sorts last descending)
                InOrder(Both(Org(), query, ordered: true), r => (string)Value(r, "nm")!));
        }

        [Fact]
        public void Aggregate_min_and_max_are_copies_not_the_stored_values()
        {
            InMemoryOrganizationService org = Org();
            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Min, "min"));
            query.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Max, "max"));

            Entity row = Assert.Single(org.RetrieveMultiple(query).Entities);
            ((Money)Value(row, "min")!).Value = -1m;
            ((Money)Value(row, "max")!).Value = -1m;

            Assert.Equal(100m, org.Retrieve("account", Acme, new ColumnSet("revenue")).GetAttributeValue<Money>("revenue").Value);
            Assert.Equal(500m, org.Retrieve("account", Hooli, new ColumnSet("revenue")).GetAttributeValue<Money>("revenue").Value);
        }

        [Fact]
        public void Aggregate_min_and_max_of_a_linked_column_are_copies_not_the_stored_values()
        {
            InMemoryOrganizationService org = Org();
            QueryExpression query = Aggregate();
            LinkEntity contacts = ContactLink(query, JoinOperator.Inner);
            contacts.Columns.AttributeExpressions.Add(Agg("parentcustomerid", XrmAggregateType.Min, "min"));
            contacts.Columns.AttributeExpressions.Add(Agg("parentcustomerid", XrmAggregateType.Max, "max"));

            Entity row = Assert.Single(org.RetrieveMultiple(query).Entities);
            Assert.Equal(Acme, ((EntityReference)Value(row, "min")!).Id);
            Assert.Equal(Globex, ((EntityReference)Value(row, "max")!).Id);
            ((EntityReference)Value(row, "min")!).Id = Guid.Empty;
            ((EntityReference)Value(row, "max")!).Id = Guid.Empty;

            Assert.Equal(Acme, org.Retrieve("contact", Wile, new ColumnSet("parentcustomerid")).GetAttributeValue<EntityReference>("parentcustomerid").Id);
            Assert.Equal(Globex, org.Retrieve("contact", Hank, new ColumnSet("parentcustomerid")).GetAttributeValue<EntityReference>("parentcustomerid").Id);
        }

        [Fact]
        public void Match_first_row_returns_the_parent_once_even_when_a_nested_outer_link_multiplies_it()
        {
            InMemoryOrganizationService org = Org();
            org.Create(Task("Callback", Wile)); // Wile now has two tasks

            QueryExpression query = Accounts("name");
            query.Criteria.AddCondition("name", ConditionOperator.Equal, "Acme");
            TaskLink(ContactLink(query, JoinOperator.MatchFirstRowUsingCrossApply, "c", "fullname"), JoinOperator.LeftOuter, "t", "subject");

            Assert.Equal(new[] { "Acme|Wile|Call" }, InOrder(Both(org, query), r => Name(r) + "|" + Aliased(r, "c.fullname") + "|" + Aliased(r, "t.subject")));
        }

        [Fact]
        public void Aggregate_fetch_whose_only_link_has_no_aggregate_is_rejected()
        {
            string fetch = "<fetch aggregate='true'><entity name='account'><link-entity name='contact' from='parentcustomerid' to='accountid'/></entity></fetch>";
            Assert.Contains("at least one attribute", Assert.Throws<NotSupportedException>(() => Fetch(Org(), fetch)).Message);
        }

        [Fact]
        public void A_link_with_all_columns_returns_every_linked_column()
        {
            QueryExpression query = Accounts("name");
            query.Criteria.AddCondition("name", ConditionOperator.Equal, "Globex");
            ContactLink(query, JoinOperator.Inner).Columns = new ColumnSet(true);

            Assert.Equal(new[] { "Gail|-", "Hank|QA" }, Sorted(Both(Org(), query), r => Aliased(r, "c.fullname") + "|" + Aliased(r, "c.jobtitle")));
        }

        [Fact]
        public void Joined_rows_with_all_columns_expose_only_real_and_aliased_columns()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, "Acme");
            TaskLink(ContactLink(query, JoinOperator.Inner, "c", "fullname"), JoinOperator.Inner, "t", "subject");

            foreach (Entity row in Both(Org(), query).Entities)
            {
                Assert.Equal(
                    new[] { "accountid", "c.fullname", "createdon", "name", "new_target", "numberofemployees", "revenue", "t.subject" },
                    row.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public void Root_column_alias_is_omitted_when_the_column_is_null()
        {
            QueryExpression query = Accounts("name");
            query.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "revenue", Alias = "rev" });

            Assert.Equal(
                new[] { "Acme|$100", "Globex|$200", "Hooli|$500", "Initech|-", "Umbrella|$400" },
                Sorted(Both(Org(), query), r => Name(r) + "|" + Aliased(r, "rev")));
        }

        [Fact]
        public void Aggregating_a_secured_column_on_a_nested_link_is_rejected()
        {
            Guid user = Guid.NewGuid();
            Guid bu = Guid.NewGuid();
            InMemoryOrganizationService org = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .WithSecurity(s => s
                    .BusinessUnit(bu)
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read)
                        .Grant("contact", AccessLevel.Organization, Privilege.Read).Grant("task", AccessLevel.Organization, Privilege.Read))
                    .User(user, bu, "reader")
                    .SecureColumn("task", "subject"))
                .Seed(Owned(Account(Acme, "Acme", 1, 1, 1m, null), user, bu), Owned(Contact(Wile, "Wile", Acme, 0, "Dev", null), user, bu), Owned(Task("Call", Wile), user, bu));

            // A null root ColumnSet: the only aggregate (and the secured column) is on the nested link.
            QueryExpression query = new QueryExpression("account") { ColumnSet = null };
            TaskLink(ContactLink(query, JoinOperator.Inner), JoinOperator.Inner).Columns.AttributeExpressions.Add(Agg("subject", XrmAggregateType.CountColumn, "n"));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => org.RetrieveMultiple(query));
        }

        // ---- fetch-level options -------------------------------------------------------------------------------

        [Fact]
        public void Returntotalrecordcount_reports_the_total()
        {
            TypedQuery<RtAccount> query = Query.For<RtAccount>().Select(a => a.Name).WithTotalRecordCount();
            EntityCollection viaFetch = Fetch(Org(), query.ToFetchXml());

            Assert.Equal(5, viaFetch.TotalRecordCount);
            Assert.Equal(5, viaFetch.Entities.Count);
        }

        [Fact]
        public void Page_without_count_uses_the_default_page_size()
        {
            Assert.Equal(5, Fetch(Org(), "<fetch page='1'><entity name='account'><attribute name='name'/></entity></fetch>").Entities.Count);
            Assert.Empty(Fetch(Org(), "<fetch page='2'><entity name='account'><attribute name='name'/></entity></fetch>").Entities);
        }

        [Fact]
        public void Hint_and_envelope_attributes_are_accepted_without_changing_the_rows()
        {
            string fetch = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' no-lock='true' latematerialize='true'
                                    options='OptimizeForUnknown' useraworderby='true' paging-cookie='x' count='10' page='1'
                                    xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>
                  <entity name='account'>
                    <attribute name='name'/>
                    <filter type='and' hint='union' isquickfindfields='true'>
                      <condition attribute='accountid' operator='in' uiname='Acme' uitype='account'>
                        <value uiname='Acme' uitype='account'>" + Acme + @"</value>
                        <value>" + Globex + @"</value>
                      </condition>
                    </filter>
                    <link-entity name='contact' from='parentcustomerid' to='accountid' alias='c' visible='false' intersect='true'>
                      <filter><condition attribute='fullname' operator='ne' value='Road' uihidden='1'/></filter>
                    </link-entity>
                  </entity>
                </fetch>";

            Assert.Equal(new[] { "Acme", "Globex", "Globex" }, Names(Fetch(Org(), fetch)));
        }

        [Theory]
        [InlineData("true", 2)]
        [InlineData("TRUE", 2)]
        [InlineData("1", 2)]
        [InlineData("false", 4)]
        [InlineData("0", 4)]
        public void Boolean_attributes_accept_true_false_and_1_0(string value, int expected)
        {
            string fetch = $"<fetch distinct='{value}'><entity name='account'><attribute name='name'/>"
                + "<link-entity name='contact' from='parentcustomerid' to='accountid'/></entity></fetch>";

            Assert.Equal(expected, Fetch(Org(), fetch).Entities.Count);
        }

        [Fact]
        public void Descending_order_inside_a_link_entity_is_parsed()
        {
            string fetch = "<fetch><entity name='account'><attribute name='name'/>"
                + "<link-entity name='contact' from='parentcustomerid' to='accountid' alias='c'><attribute name='fullname'/><order attribute='fullname' descending='true'/></link-entity>"
                + "</entity></fetch>";

            Assert.Equal(new[] { "Wile", "Road", "Hank", "Gail" }, InOrder(Fetch(Org(), fetch), r => Aliased(r, "c.fullname")));
        }

        [Fact]
        public void Retained_data_source_is_rejected_on_both_paths()
        {
            TypedQuery<RtAccount> typed = Query.For<RtAccount>().Select(a => a.Name).FromRetainedData();
            Assert.Contains("'datasource'", Assert.Throws<NotSupportedException>(() => Fetch(Org(), typed.ToFetchXml())).Message);

            QueryExpression query = Accounts("name");
            query.DataSource = "retained";
            Assert.Contains("'retained'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(query)).Message);
        }

        [Fact]
        public void Natural_join_is_rejected_on_both_paths()
        {
            QueryExpression query = Accounts("name");
            ContactLink(query, JoinOperator.Natural);

            Assert.Contains("'Natural'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(query)).Message);
            Assert.Contains("'natural'", Assert.Throws<NotSupportedException>(() => Fetch(Org(), query.ToFetchXml())).Message);
        }

        // ---- QueryExpression aggregate shape checks --------------------------------------------------------------

        [Fact]
        public void Aggregate_with_plain_columns_is_rejected()
        {
            QueryExpression named = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            named.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            Assert.Contains("'account'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(named)).Message);

            QueryExpression all = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            all.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            Assert.Contains("'account'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(all)).Message);

            QueryExpression linked = Aggregate();
            linked.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(linked, JoinOperator.Inner, "c", "fullname");
            Assert.Contains("'contact'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(linked)).Message);
        }

        [Fact]
        public void Aggregate_with_a_null_root_column_set_aggregates_the_link_columns()
        {
            QueryExpression query = new QueryExpression("account") { ColumnSet = null };
            ContactLink(query, JoinOperator.Inner).Columns.AttributeExpressions.Add(Agg("fullname", XrmAggregateType.CountColumn, "n"));

            Assert.Equal(4, Value(Assert.Single(Org().RetrieveMultiple(query).Entities), "n"));
        }

        [Fact]
        public void Aggregate_shape_errors_are_reported()
        {
            QueryExpression noAlias = Aggregate();
            noAlias.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Sum, string.Empty));
            Assert.Contains("'revenue'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(noAlias)).Message);

            QueryExpression plainAlias = Aggregate();
            plainAlias.ColumnSet.AttributeExpressions.Add(Agg("revenue", XrmAggregateType.Sum, "s"));
            plainAlias.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression { AttributeName = "name", Alias = "plain" });
            Assert.Contains("'plain'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(plainAlias)).Message);

            QueryExpression badOrder = Aggregate();
            badOrder.ColumnSet.AttributeExpressions.Add(Group("name", "nm"));
            badOrder.Orders.Add(new OrderExpression { Alias = "zzz" });
            Assert.Contains("'zzz'", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(badOrder)).Message);

            QueryExpression linkOrder = Aggregate();
            linkOrder.ColumnSet.AttributeExpressions.Add(Group("name", "nm"));
            ContactLink(linkOrder, JoinOperator.Inner).Orders.Add(new OrderExpression("fullname", OrderType.Ascending));
            Assert.Contains("link-entity", Assert.Throws<NotSupportedException>(() => Org().RetrieveMultiple(linkOrder)).Message);
        }

        // ---- field security on linked aggregate columns -------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Aggregating_a_secured_linked_column_is_rejected(bool groupBy)
        {
            Guid user = Guid.NewGuid();
            Guid bu = Guid.NewGuid();
            InMemoryOrganizationService org = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .WithSecurity(s => s
                    .BusinessUnit(bu)
                    .Role("reader", r => r.Grant("account", AccessLevel.Organization, Privilege.Read).Grant("contact", AccessLevel.Organization, Privilege.Read))
                    .User(user, bu, "reader")
                    .SecureColumn("contact", "jobtitle"))
                .Seed(Owned(Account(Acme, "Acme", 1, 1, 1m, null), user, bu), Owned(Contact(Wile, "Wile", Acme, 0, "Dev", null), user, bu));

            QueryExpression query = Aggregate();
            query.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(query, JoinOperator.Inner).Columns.AttributeExpressions.Add(groupBy ? Group("jobtitle", "j") : Agg("jobtitle", XrmAggregateType.CountColumn, "j"));

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => org.RetrieveMultiple(query));
            Assert.Throws<FaultException<OrganizationServiceFault>>(() => org.RetrieveMultiple(new FetchExpression(query.ToFetchXml())));

            // A non-secured linked column is fine.
            QueryExpression open = Aggregate();
            open.ColumnSet.AttributeExpressions.Add(Agg("accountid", XrmAggregateType.Count, "n"));
            ContactLink(open, JoinOperator.Inner).Columns.AttributeExpressions.Add(Agg("fullname", XrmAggregateType.CountColumn, "f"));
            Assert.Equal(1, Value(Assert.Single(org.RetrieveMultiple(open).Entities), "f"));
        }

        private static Entity Owned(Entity record, Guid user, Guid bu)
        {
            record["ownerid"] = new EntityReference("systemuser", user);
            record["owningbusinessunit"] = new EntityReference("businessunit", bu);
            return record;
        }

        // ---- every unsupported element / attribute is rejected, naming it ---------------------------------------

        private const string Link = "<link-entity name='contact' from='parentcustomerid' to='accountid' alias='c'>";

        [Theory]
        [InlineData("<query/>", "<query>")]
        [InlineData("<fetch/>", "exactly one <entity>")]
        [InlineData("<fetch><entity name='account'/><entity name='contact'/></fetch>", "exactly one <entity>")]
        [InlineData("<fetch><order attribute='name'/></fetch>", "<order>")]
        [InlineData("<fetch foo='1'><entity name='account'/></fetch>", "'foo' on <fetch>")]
        [InlineData("<fetch datasource='retained'><entity name='account'/></fetch>", "'datasource' on <fetch>")]
        [InlineData("<fetch distinct='yes'><entity name='account'/></fetch>", "distinct='yes'")]
        [InlineData("<fetch aggregatelimit='5'><entity name='account'/></fetch>", "aggregatelimit")]
        [InlineData("<fetch><entity/></fetch>", "'name'")]
        [InlineData("<fetch><entity name=''/></fetch>", "'name'")]
        [InlineData("<fetch><entity name='account' enableprefiltering='1'/></fetch>", "'enableprefiltering' on <entity>")]
        [InlineData("<fetch><entity name='account'><bogus/></entity></fetch>", "<bogus> inside <entity>")]
        [InlineData("<fetch><entity name='account'><all-attributes foo='1'/></entity></fetch>", "'foo' on <all-attributes>")]
        [InlineData("<fetch><entity name='account'><all-attributes><x/></all-attributes></entity></fetch>", "<x> inside <all-attributes>")]
        [InlineData("<fetch><entity name='account'><attribute/></entity></fetch>", "'name'")]
        [InlineData("<fetch><entity name='account'><attribute name='name' build='1'/></entity></fetch>", "'build' on <attribute>")]
        [InlineData("<fetch><entity name='account'><attribute name='name'><x/></attribute></entity></fetch>", "<x> inside <attribute>")]
        [InlineData("<fetch><entity name='account'><attribute name='parentaccountid' alias='n' rowaggregate='CountChildren'/></entity></fetch>", "'rowaggregate'")]
        [InlineData("<fetch><entity name='account'><attribute name='name' aggregate='count'/></entity></fetch>", "'aggregate' on <attribute> requires")]
        [InlineData("<fetch><entity name='account'><attribute name='name' groupby='true'/></entity></fetch>", "'groupby' on <attribute> requires")]
        [InlineData("<fetch><entity name='account'><attribute name='name' dategrouping='year'/></entity></fetch>", "'dategrouping' on <attribute> requires")]
        [InlineData("<fetch><entity name='account'><attribute name='name' usertimezone='false'/></entity></fetch>", "'usertimezone' on <attribute> requires")]
        [InlineData("<fetch><entity name='account'><attribute name='name' distinct='true'/></entity></fetch>", "'distinct' on <attribute> requires")]
        [InlineData("<fetch><entity name='account'><filter foo='x'/></entity></fetch>", "'foo' on <filter>")]
        [InlineData("<fetch><entity name='account'><filter type='xor'/></entity></fetch>", "'xor'")]
        [InlineData("<fetch><entity name='account'><filter><bogus/></filter></entity></fetch>", "<bogus> inside <filter>")]
        [InlineData("<fetch><entity name='account'><filter>" + Link + "</link-entity></filter></entity></fetch>", "(got 'inner')")]
        [InlineData("<fetch><entity name='account'><filter><link-entity name='contact' from='parentcustomerid' to='accountid' link-type='exists'/></filter></entity></fetch>", "(got 'exists')")]
        [InlineData("<fetch><entity name='account'><filter><condition operator='null'/></filter></entity></fetch>", "'attribute'")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name'/></filter></entity></fetch>", "'operator'")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='eq' value='x' foo='y'/></filter></entity></fetch>", "'foo' on <condition>")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='eq'><bogus/></condition></filter></entity></fetch>", "<bogus> inside <condition>")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='in'><value foo='1'>x</value></condition></filter></entity></fetch>", "'foo' on <value>")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='in'><value><x/></value></condition></filter></entity></fetch>", "<x> inside <value>")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='eq' value='x' valueof='y'/></filter></entity></fetch>", "'name' cannot combine valueof")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='eq' valueof='y'><value>x</value></condition></filter></entity></fetch>", "'name' cannot combine valueof")]
        [InlineData("<fetch><entity name='account'><filter><condition attribute='name' operator='sounds-like' value='x'/></filter></entity></fetch>", "'sounds-like'")]
        [InlineData("<fetch><entity name='account'><order attribute='name' foo='1'/></entity></fetch>", "'foo' on <order>")]
        [InlineData("<fetch><entity name='account'><order attribute='name'><x/></order></entity></fetch>", "<x> inside <order>")]
        [InlineData("<fetch><entity name='account'><order alias='nm'/></entity></fetch>", "alias='nm'")]
        [InlineData("<fetch><entity name='account'><order descending='true'/></entity></fetch>", "requires an attribute")]
        [InlineData("<fetch><entity name='account'>" + Link + "<order attribute='fullname' entityname='c'/></link-entity></entity></fetch>", "<order entityname>")]
        [InlineData("<fetch><entity name='account'><link-entity name='contact' from='parentcustomerid' to='accountid' foo='1'/></entity></fetch>", "'foo' on <link-entity>")]
        [InlineData("<fetch><entity name='account'>" + Link + "<bogus/></link-entity></entity></fetch>", "<bogus> inside <link-entity>")]
        [InlineData("<fetch><entity name='account'><link-entity name='contact' from='parentcustomerid' to='accountid' link-type='natural'/></entity></fetch>", "'natural'")]
        [InlineData("<fetch><entity name='account'><link-entity name='contact' from='parentcustomerid' to='accountid' link-type='Outer'/></entity></fetch>", "'Outer'")]
        [InlineData("<fetch><entity name='account'><link-entity from='parentcustomerid' to='accountid'/></entity></fetch>", "'name'")]
        [InlineData("<fetch><entity name='account'><link-entity name='contact' to='accountid'/></entity></fetch>", "'from'")]
        [InlineData("<fetch><entity name='account'><link-entity name='contact' from='parentcustomerid'/></entity></fetch>", "'to'")]
        public void Unsupported_FetchXml_is_rejected_naming_the_construct(string fetchXml, string expected)
        {
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Fetch(Org(), fetchXml));
            Assert.Contains(expected, error.Message);
        }

        [Theory]
        [InlineData("<attribute name='name' alias='nm' groupby='true'/><all-attributes/>", "<all-attributes>")]
        [InlineData("<attribute name='name' groupby='true'/>", "'name' requires an alias")]
        [InlineData("<attribute name='name' alias='nm'/>", "'name' must have aggregate or groupby")]
        [InlineData("<attribute name='name' alias='nm' aggregate='median'/>", "'median'")]
        [InlineData("<attribute name='revenue' alias='s' aggregate='sum' distinct='true'/>", "not 'sum'")]
        [InlineData("<attribute name='name' alias='nm' groupby='true' aggregate='count'/>", "'name' cannot be both")]
        [InlineData("<attribute name='name' alias='nm' groupby='true' distinct='true'/>", "'name' cannot be both")]
        [InlineData("<attribute name='createdon' alias='y' groupby='true' dategrouping='decade'/>", "'decade'")]
        [InlineData("<attribute name='createdon' alias='y' aggregate='max' dategrouping='year'/>", "on 'createdon' require groupby")]
        [InlineData("<attribute name='createdon' alias='y' aggregate='max' usertimezone='false'/>", "on 'createdon' require groupby")]
        [InlineData("<filter><condition attribute='name' operator='not-null'/></filter>", "at least one attribute")]
        [InlineData("<attribute name='name' alias='nm' groupby='true'/><order alias='zzz'/>", "'zzz'")]
        [InlineData("<attribute name='name' alias='nm' groupby='true'/><order attribute='nm' entityname='c'/>", "root <entity> only")]
        [InlineData("<attribute name='name' alias='nm' groupby='true'/>" + Link + "<order alias='nm'/></link-entity>", "root <entity> only")]
        [InlineData("<attribute name='name' alias='nm' groupby='true'/>" + Link + "<attribute name='fullname' alias='f'/></link-entity>", "'fullname' must have aggregate or groupby")]
        public void Unsupported_aggregate_FetchXml_is_rejected_naming_the_construct(string body, string expected)
        {
            string fetchXml = "<fetch aggregate='true'><entity name='account'>" + body + "</entity></fetch>";
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Fetch(Org(), fetchXml));
            Assert.Contains(expected, error.Message);
        }

        [Fact]
        public void Aggregate_order_by_attribute_or_alias_is_parsed()
        {
            string Fetch2(string order) => "<fetch aggregate='true'><entity name='account'><attribute name='name' alias='nm' groupby='true'/>"
                + order + "</entity></fetch>";

            Assert.Equal("Umbrella", Value(Fetch(Org(), Fetch2("<order alias='nm' descending='true'/>")).Entities[0], "nm"));
            Assert.Equal("Umbrella", Value(Fetch(Org(), Fetch2("<order attribute='nm' descending='true'/>")).Entities[0], "nm"));
            Assert.Equal("Acme", Value(Fetch(Org(), Fetch2("<order alias='nm'/>")).Entities[0], "nm"));
        }

        [Fact]
        public void Aggregate_only_on_a_link_entity_is_parsed()
        {
            string fetch = "<fetch aggregate='true'><entity name='account'>"
                + "<link-entity name='contact' from='parentcustomerid' to='accountid'><link-entity name='task' from='regardingobjectid' to='contactid' alias='t'>"
                + "<attribute name='subject' alias='n' aggregate='countcolumn'/></link-entity></link-entity></entity></fetch>";

            Assert.Equal(3, Value(Assert.Single(Fetch(Org(), fetch).Entities), "n"));
        }
    }
}
