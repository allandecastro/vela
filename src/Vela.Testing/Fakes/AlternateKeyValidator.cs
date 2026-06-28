using System;
using System.Collections.Generic;
using System.Linq;
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
                    throw new InvalidOperationException(
                        $"A duplicate {entity.LogicalName} record already exists for alternate key ({string.Join(", ", key)}).");
                }
            }
        }
    }
}
