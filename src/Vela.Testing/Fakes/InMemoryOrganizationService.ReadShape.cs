using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Testing
{
    // The shape of a record as Dataverse returns it from Retrieve/RetrieveMultiple: the primary key column is always
    // present ({table}id, or the metadata's PrimaryIdAttribute), a lookup carries the referenced record's primary name
    // (EntityReference.Name, also as its formatted value), and a choice carries its label as a formatted value when
    // the table's metadata (WithEntityMetadata) declares the option.
    public sealed partial class InMemoryOrganizationService
    {
        // Activity tables share the activitypointer primary key.
        private static readonly HashSet<string> ActivityTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "activitypointer", "appointment", "email", "fax", "letter", "phonecall", "task", "recurringappointmentmaster",
            "serviceappointment", "campaignactivity", "campaignresponse", "socialactivity", "chat",
        };

        // Primary-name column of common system tables whose primary name is not "name".
        private static readonly Dictionary<string, string> KnownPrimaryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = "fullname",
            ["lead"] = "fullname",
            ["systemuser"] = "fullname",
            ["incident"] = "title",
            ["annotation"] = "subject",
        };

        internal string PrimaryIdAttribute(string logicalName)
        {
            if (_metadata.TryGetValue(logicalName, out EntityMetadata? metadata) && !string.IsNullOrEmpty(metadata.PrimaryIdAttribute))
            {
                return metadata.PrimaryIdAttribute;
            }

            return ActivityTables.Contains(logicalName) ? "activityid" : logicalName + "id";
        }

        internal string PrimaryNameAttributeName(string logicalName) => PrimaryNameAttribute(logicalName);

        private string PrimaryNameAttribute(string logicalName)
        {
            if (_metadata.TryGetValue(logicalName, out EntityMetadata? metadata) && !string.IsNullOrEmpty(metadata.PrimaryNameAttribute))
            {
                return metadata.PrimaryNameAttribute;
            }

            if (KnownPrimaryNames.TryGetValue(logicalName, out string? known))
            {
                return known;
            }

            return ActivityTables.Contains(logicalName) ? "subject" : "name";
        }

        // Applied to every row a read returns (never to stored rows): adds the primary key, lookup names and formatted
        // values. Aggregate rows (no id) are left alone.
        private void ShapeRead(Entity row)
        {
            if (row.Id == Guid.Empty || string.IsNullOrEmpty(row.LogicalName))
            {
                return;
            }

            string primaryId = PrimaryIdAttribute(row.LogicalName);
            if (!row.Attributes.ContainsKey(primaryId))
            {
                row[primaryId] = row.Id;
            }

            _metadata.TryGetValue(row.LogicalName, out EntityMetadata? metadata);
            foreach (KeyValuePair<string, object> attribute in row.Attributes.ToList())
            {
                switch (attribute.Value)
                {
                    case EntityReference reference when reference.Id != Guid.Empty:
                        if (reference.Name == null && ReferencedName(reference) is string name)
                        {
                            reference.Name = name;
                        }

                        if (reference.Name != null)
                        {
                            row.FormattedValues[attribute.Key] = reference.Name;
                        }

                        break;
                    case OptionSetValue option when OptionLabel(metadata, attribute.Key, option.Value) is string label:
                        row.FormattedValues[attribute.Key] = label;
                        break;
                }
            }
        }

        private string? ReferencedName(EntityReference reference)
        {
            if (string.IsNullOrEmpty(reference.LogicalName)
                || !_store.TryGetValue(reference.LogicalName, out Dictionary<Guid, Entity>? table)
                || !table.TryGetValue(reference.Id, out Entity? referenced))
            {
                return null;
            }

            string? nameAttribute = PrimaryNameAttribute(reference.LogicalName);
            return nameAttribute != null && referenced.Attributes.TryGetValue(nameAttribute, out object? value) ? value as string : null;
        }

        private static string? OptionLabel(EntityMetadata? metadata, string attributeName, int value)
        {
            AttributeMetadata? attribute = metadata?.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, attributeName, StringComparison.OrdinalIgnoreCase));
            OptionSetMetadata? optionSet = (attribute as EnumAttributeMetadata)?.OptionSet;
            OptionMetadata? option = optionSet?.Options?.FirstOrDefault(o => o.Value == value);
            return option?.Label?.UserLocalizedLabel?.Label ?? option?.Label?.LocalizedLabels?.FirstOrDefault()?.Label;
        }
    }
}
