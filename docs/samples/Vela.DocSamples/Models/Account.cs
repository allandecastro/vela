namespace Vela.DocSamples.Models
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    // A minimal early-bound model, the shape `vela models` generates.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Account : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public Account()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("accountid")]
        public override Guid Id
        {
            get => base.Id;
            set => base.Id = value;
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [AttributeLogicalName("creditlimit")]
        public Money CreditLimit
        {
            get => GetAttributeValue<Money>("creditlimit");
            set => SetAttributeValue("creditlimit", value);
        }

        [AttributeLogicalName("description")]
        public string Description
        {
            get => GetAttributeValue<string>("description");
            set => SetAttributeValue("description", value);
        }
    }
}
