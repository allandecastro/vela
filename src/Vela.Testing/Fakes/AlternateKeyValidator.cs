using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// Holds the alternate keys declared on <see cref="InMemoryOrganizationService"/> and enforces their
    /// uniqueness. On Create/Update, when all of a key's attributes are populated, a matching record on another
    /// row triggers the same duplicate-key fault the platform would raise.
    /// </summary>
    internal sealed class AlternateKeyValidator
    {
        private readonly Dictionary<string, List<string[]>> _keys =
            new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);

        public void Add(string entityLogicalName, string[] keyAttributes)
        {
            if (keyAttributes == null || keyAttributes.Length == 0)
            {
                throw new ArgumentException("An alternate key needs at least one attribute.", nameof(keyAttributes));
            }

            if (!_keys.TryGetValue(entityLogicalName, out List<string[]>? keys))
            {
                keys = new List<string[]>();
                _keys[entityLogicalName] = keys;
            }

            keys.Add(keyAttributes);
        }

        /// <summary>
        /// Throws the platform's <c>0x80060889</c> unless <paramref name="keyAttributes"/> are exactly the attributes
        /// of one declared key (verified live: an undeclared key, or a declared one plus an extra attribute, is
        /// rejected, also on a table with no key at all).
        /// </summary>
        public void DemandDeclared(string entityLogicalName, ICollection<string> keyAttributes)
        {
            List<string[]> keys = _keys.TryGetValue(entityLogicalName, out List<string[]>? declared) ? declared : new List<string[]>();
            if (keys.Any(key => key.Length == keyAttributes.Count && key.All(attribute => keyAttributes.Contains(attribute, StringComparer.OrdinalIgnoreCase))))
            {
                return;
            }

            string declaredKeys = string.Join(", ", keys.Select(key => string.Join("; ", key)));
            string message = $"The specified key attributes '{string.Join("; ", keyAttributes)}' are not defined as keys for entity "
                + $"'{entityLogicalName}', which has entity keys '{declaredKeys}'. Declare the key with WithAlternateKey(\"{entityLogicalName}\", ...).";
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80060889), Message = message },
                new FaultReason(message));
        }

        /// <summary>Throws when <paramref name="entity"/> would collide with one of <paramref name="existing"/> on any declared key.</summary>
        public void Enforce(Entity entity, IEnumerable<Entity> existing)
        {
            if (!_keys.TryGetValue(entity.LogicalName, out List<string[]>? keys))
            {
                return;
            }

            List<Entity> rows = existing.ToList();
            foreach (string[] key in keys)
            {
                // The platform only enforces a key once all of its attributes are populated.
                if (!key.All(attribute => EntityValues.GetValue(entity, attribute) != null))
                {
                    continue;
                }

                bool duplicate = rows.Any(other =>
                    other.Id != entity.Id &&
                    key.All(attribute => EntityValues.AreEqual(EntityValues.GetValue(other, attribute), EntityValues.GetValue(entity, attribute))));

                if (duplicate)
                {
                    // 0x80060892 DuplicateRecordKeyValueExists - the platform's alternate-key duplicate fault, so a
                    // test asserting on the fault/ErrorCode behaves the same against a real org.
                    string message = $"A duplicate {entity.LogicalName} record already exists for alternate key ({string.Join(", ", key)}).";
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = unchecked((int)0x80060892), Message = message },
                        new FaultReason(message));
                }
            }
        }
    }
}
