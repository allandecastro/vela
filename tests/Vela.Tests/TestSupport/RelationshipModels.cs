using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Tests.TestSupport;

// Mirrors the generated RelationshipSchemaNameAttribute (which lives in the consumer's namespace), so the runtime
// guardrail and RelationshipProjector - both of which match it by name - can be exercised in tests.
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RelationshipSchemaNameAttribute : Attribute
{
    public RelationshipSchemaNameAttribute(string schemaName) => SchemaName = schemaName ?? string.Empty;

    public string SchemaName { get; }
}

// An early-bound account with a scalar column, a lookup FK, and the two generated relationship nav shapes.
[EntityLogicalName(EntityLogicalNameValue)]
public sealed class RelAccount : Entity
{
    public const string EntityLogicalNameValue = "account";

    public RelAccount()
        : base(EntityLogicalNameValue)
    {
    }

    [AttributeLogicalName("name")]
    public string Name
    {
        get => GetAttributeValue<string>("name");
        set => SetAttributeValue("name", value);
    }

    [AttributeLogicalName("primarycontactid")]
    public EntityReference PrimaryContactId
    {
        get => GetAttributeValue<EntityReference>("primarycontactid");
        set => SetAttributeValue("primarycontactid", value);
    }

    [RelationshipSchemaName("account_primary_contact")]
    public RelContact? PrimaryContact { get; set; }

    [RelationshipSchemaName("contact_customer_accounts")]
    public IList<RelContact> Contacts { get; set; } = new List<RelContact>();
}

[EntityLogicalName(EntityLogicalNameValue)]
public sealed class RelContact : Entity
{
    public const string EntityLogicalNameValue = "contact";

    public RelContact()
        : base(EntityLogicalNameValue)
    {
    }

    [AttributeLogicalName("firstname")]
    public string FirstName
    {
        get => GetAttributeValue<string>("firstname");
        set => SetAttributeValue("firstname", value);
    }

    [AttributeLogicalName("parentcustomerid")]
    public EntityReference ParentCustomerId
    {
        get => GetAttributeValue<EntityReference>("parentcustomerid");
        set => SetAttributeValue("parentcustomerid", value);
    }
}
