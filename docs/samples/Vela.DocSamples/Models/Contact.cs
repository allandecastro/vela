namespace Vela.DocSamples.Models
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    // A minimal early-bound model, the shape `vela models` generates.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Contact : Entity
    {
        public const string EntityLogicalNameValue = "contact";

        public Contact()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("contactid")]
        public override Guid Id
        {
            get => base.Id;
            set => base.Id = value;
        }
    }
}
